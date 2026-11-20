"""Generate the branded TSF source tree from the pinned upstream checkout."""

import argparse
import re
import shutil
import subprocess
from html import escape
from io import BytesIO
from pathlib import Path
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]
WEASEL_REVISION = '9cc96e20dc71b80876b12f689bb5863c76c2a7ed'


def replace(path, before, after, expected=1):
    source = path.read_text(encoding='utf-8-sig')
    if source.count(before) != expected:
        raise RuntimeError(f'{path.name}: expected {expected} occurrences of {before[:100]!r}')
    path.write_text(source.replace(before, after), encoding='utf-8')


def replace_pattern(path, pattern, after):
    source = path.read_text(encoding='utf-8-sig')
    source, count = re.subn(pattern, after if callable(after) else lambda _: after, source)
    if count != 1:
        raise RuntimeError(f'{path.name}: expected one match for {pattern!r}')
    path.write_text(source, encoding='utf-8')


def write_overlay(source, version, boost):
    native = ROOT / 'windows/Native'
    for name, destination in (
        ('Register.cpp', 'WeaselTSF/Register.cpp'),
        ('ServerApp.cpp', 'WeaselServer/WeaselServerApp.cpp'),
        ('Deployer.cpp', 'WeaselDeployer/WeaselDeployer.cpp'),
    ):
        shutil.copyfile(native / name, source / destination)
    shutil.copyfile(ROOT / 'windows/Link/ShuangpinLink.ico', source / 'resource/weasel.ico')
    for name in ('en', 'zh', 'full', 'half', 'reload'):
        shutil.copyfile(native / 'icons' / f'{name}.ico', source / 'resource' / f'{name}.ico')

    properties = (source / 'weasel.props.template').read_text(encoding='utf-8')
    major, minor, patch = version.split('.')
    fields = dict(BOOST_ROOT=boost, PLATFORM_TOOLSET='v143', VERSION_MAJOR=major,
                  VERSION_MINOR=minor, VERSION_PATCH=patch, PRODUCT_VERSION=f'{version}.0',
                  FILE_VERSION=f'{version}.0')
    for key, value in fields.items():
        properties = properties.replace(f'${key}', escape(str(value)))
    (source / 'weasel.props').write_text(properties, encoding='utf-8')

    style = source / 'output/data/weasel.yaml'
    replace(style, '# Weasel settings', '# 双拼互联候选窗配置')
    replace_pattern(style, r'(?ms)^app_options:\n.*?(?=^show_notifications:)', '')

    constants = source / 'include/WeaselConstants.h'
    replace(constants, '"Weasel"', '"ShuangpinLink"')
    replace(constants, r'Software\\Rime\\Weasel', r'Software\\ShuangpinLink\\IME')
    replace(constants, r'Software\\Rime"', r'Software\\ShuangpinLink"')
    utility = source / 'include/WeaselUtility.h'
    replace_pattern(utility, r'(?ms)^inline std::wstring get_weasel_ime_name\(\).*?^}',
                    'inline std::wstring get_weasel_ime_name() { return L"双拼互联"; }')
    replace(utility, r'Software\\Rime\\Weasel', r'Software\\ShuangpinLink\\IME')
    replace(utility, 'rime.weasel', 'rime.shuangpin_link')
    replace(utility, '// data directories\n', r'''inline bool CanLaunchUserProcess() {
  HANDLE token = nullptr;
  if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    return false;
  TOKEN_ELEVATION elevation{};
  DWORD size = 0;
  const BOOL success = GetTokenInformation(token, TokenElevation, &elevation,
                                           sizeof(elevation), &size);
  CloseHandle(token);
  return success && !elevation.TokenIsElevated;
}

// data directories
''')
    ipc = source / 'include/WeaselIPC.h'
    replace(ipc, 'WeaselIPCWindow_1.0', 'ShuangpinLinkIPCWindow_1.0')
    replace(ipc, 'WeaselNamedPipe', 'ShuangpinLinkNamedPipe')
    replace(source / 'WeaselIPCServer/WeaselServerImpl.cpp',
            '(WEASEL)Furandōru-Sukāretto-', 'ShuangpinLinkEngine-')

    utility_cpp = source / 'RimeWithWeasel/WeaselUtility.cpp'
    replace_pattern(utility_cpp, r'(?ms)^fs::path WeaselUserDataPath\(\).*?^}', r'''
fs::path WeaselUserDataPath() {
  wchar_t path[32768]{};
  DWORD length = ExpandEnvironmentStringsW(L"%APPDATA%\\ShuangpinLink\\Rime", path, ARRAYSIZE(path));
  if (!length || length > ARRAYSIZE(path) || path[0] == L'%')
    throw std::runtime_error("Unable to obtain the user input data directory");
  return fs::path(path);
}'''.lstrip())
    handler = source / 'RimeWithWeasel/RimeWithWeasel.cpp'
    replace(handler, 'rime.weasel', 'rime.shuangpin_link', expected=8)
    replace(handler, '"weasel"', '"shuangpin_link"', expected=3)
    replace(handler, 'WeaselDeployerMutex', 'ShuangpinLinkDeployerMutex')

    tsf = source / 'WeaselTSF'
    shutil.copyfile(native / 'EnglishCorrection.h', tsf / 'EnglishCorrection.h')
    corrections = (ROOT / 'rime/english_corrections.tsv').read_text(encoding='ascii').splitlines()
    entries = [tuple(row.split('\t')) for row in corrections]
    if len({typo for typo, _ in entries}) != len(entries) or any(
            not re.fullmatch(r'[a-z]{3,24}', word) for entry in entries for word in entry):
        raise ValueError('Invalid English correction table')
    (tsf / 'LinkEnglishWords.h').write_text(
        '#pragma once\n#include <string_view>\n#include <utility>\n'
        'constexpr std::pair<std::wstring_view, std::wstring_view> kLinkEnglishWords[] = {\n'
        + ''.join(f'  {{L"{typo}", L"{spelling}"}},\n' for typo, spelling in entries) + '};\n',
        encoding='ascii')
    key_events = tsf / 'KeyEventSink.cpp'
    replace(key_events, '#include "CandidateList.h"', '#include "CandidateList.h"\n#include "EnglishCorrection.h"')
    for test in (True, False):
        action = 'OnTestKeyDown' if test else 'OnKeyDown'
        # Some TSF hosts only call OnTestKeyDown. Follow the normal input path
        # and use its pending flag so OnKeyDown cannot apply the same space twice.
        pending = '\n      _fTestKeyDownPending = TRUE;' if test else ''
        replace_pattern(key_events, rf'(?m)^(STDAPI WeaselTSF::{action}\([^{{]+\{{)\n',
            lambda match: match[1] + f'''
  if (wParam == VK_SPACE && !_fTestKeyDownPending)
    link_english::Trace("key event, ascii mode", S_OK, _status.ascii_mode);
  if (wParam == VK_SPACE && pContext && _status.ascii_mode && !_fTestKeyDownPending &&
      !(_isToOpenClose && !_IsKeyboardOpen()) && !_IsKeyboardDisabled() &&
      link_english::EnabledForSpace(lParam)) {{
    if (!_status.composing && link_english::HandleSpace(this, pContext, _tfClientId)) {{
      _fTestKeyUpPending = FALSE;{pending}
      *pfEaten = TRUE;
      return S_OK;
    }}
    _linkEnglishContext = pContext;
  }}
''')
    replace(tsf / 'WeaselTSF.h', '  weasel::Status _status;', '  weasel::Status _status;\n  com_ptr<ITfContext> _linkEnglishContext;')
    session = tsf / 'EditSession.cpp'
    replace(session, '#include "ResponseParser.h"', '#include "ResponseParser.h"\n#include "EnglishCorrection.h"')
    replace(session, '  bool ok = m_client.GetResponseData(std::ref(parser));', '''  bool ok = m_client.GetResponseData(std::ref(parser));
  if (_linkEnglishContext) {
    if (ok && _linkEnglishContext.p == _pEditSessionContext.p)
      link_english::CorrectCompositionCommit(_pEditSessionContext, ec, commit);
    _linkEnglishContext.Release();
  }''')
    globals_cpp = tsf / 'Globals.cpp'
    for old, new in (
        ('c_clsidTextService', '0xa6d4a312, 0x7df3, 0x4bd0, {0x9d, 0x84, 0xbb, 0xd2, 0x5b, 0xc6, 0x0a, 0xa1}'),
        ('c_guidProfile', '0x40cfbb46, 0x1eb1, 0x41f3, {0xa3, 0x36, 0x75, 0xcb, 0x9a, 0x13, 0x7b, 0xd2}'),
        ('c_guidLangBarItemButton', '0xd09bbf5c, 0x000a, 0x4970, {0xbc, 0xc0, 0x9d, 0x7c, 0xde, 0x3e, 0xdb, 0x54}'),
        ('c_guidDisplayAttributeInput', '0x04440052, 0xa9ae, 0x4868, {0xb0, 0x7a, 0xde, 0x59, 0x2f, 0x37, 0xb0, 0x35}'),
        ('GUID_IME_MODE_PRESERVED_KEY', '0xbfb3abcf, 0xd608, 0x491e, {0x88, 0x27, 0x05, 0x62, 0x8e, 0x49, 0x23, 0xe5}'),
    ):
        replace_pattern(globals_cpp, rf'(?ms)(?:// \{{[0-9A-F-]+\}}\n)?(?:static )?const GUID {old} = \{{.*?\}};',
                        f'const GUID {old} = {{{new}}};')
    replace(tsf / 'dllmain.cpp', '      SetUnhandledExceptionFilter(_UnhandledExceptionFilter);\n', '')
    replace(tsf / 'dllmain.cpp', 'rime.weasel', 'rime.shuangpin_link')
    replace(tsf / 'DisplayAttributeInfo.cpp', 'Weasel Display Attribute Input', '双拼互联输入标记')
    replace(tsf / 'WeaselTSF.cpp', r'Software\\Rime\\weasel', r'Software\\ShuangpinLink\\IME')
    replace(tsf / 'WeaselTSF.cpp', '#include <thread>\n', '#include <atomic>\n#include <thread>\n')
    replace_pattern(tsf / 'WeaselTSF.cpp', r'(?ms)^static unsigned int retry = 0;.*?^}', r'''
bool WeaselTSF::_EnsureServerConnected() {
  if (m_client.Echo())
    return true;
  _Reconnect();
  if (m_client.Echo())
    return true;
  if (!CanLaunchUserProcess())
    return false;
  static std::atomic<ULONGLONG> next_launch{0};
  const ULONGLONG now = GetTickCount64();
  auto allowed = next_launch.load();
  if (now < allowed || !next_launch.compare_exchange_strong(allowed, now + 1500))
    return false;
  const auto root = _GetRootDir();
  if (!root.empty()) {
    const auto server = std::filesystem::path(root) / L"ShuangpinImeServer.exe";
    ShellExecuteW(nullptr, L"open", server.c_str(), nullptr, root.c_str(), SW_HIDE);
  }
  return false;
}'''.lstrip())
    bar = tsf / 'LanguageBar.cpp'
    replace_pattern(bar, r'(?ms)^static LPCWSTR GetWeaselRegName\(\).*?(?=^CLangBarItemButton::CLangBarItemButton)', '')
    replace(bar, 'WeaselTSF Button', '双拼互联', expected=2)
    replace_pattern(bar, r'(?ms)^std::wstring WeaselTSF::_GetRootDir\(\).*?^}', r'''
std::wstring WeaselTSF::_GetRootDir() {
  wchar_t path[32768]{};
  const DWORD length = GetModuleFileNameW(g_hInst, path, ARRAYSIZE(path));
  if (!length || length >= ARRAYSIZE(path))
    return {};
  return std::filesystem::path(path).parent_path().wstring();
}'''.lstrip())
    replace_pattern(bar, r'(?ms)^void WeaselTSF::_HandleLangBarMenuSelect\(UINT wID\).*?^}', r'''
void WeaselTSF::_HandleLangBarMenuSelect(UINT command) {
  if (command == ID_WEASELTRAY_ENABLE_ASCII || command == ID_WEASELTRAY_DISABLE_ASCII) {
    m_client.TrayCommand(command);
    return;
  }
  if (command != ID_WEASELTRAY_SETTINGS || !CanLaunchUserProcess())
    return;
  wchar_t path[32768]{};
  const DWORD length = ExpandEnvironmentStringsW(
      L"%LOCALAPPDATA%\\ShuangpinLink\\app\\ShuangpinLink.exe", path, ARRAYSIZE(path));
  if (length && length <= ARRAYSIZE(path) && path[0] != L'%')
    ShellExecuteW(nullptr, L"open", path, nullptr, nullptr, SW_SHOWNORMAL);
}'''.lstrip())
    # The 32-bit binary registers its COM server view; the 64-bit binary owns the shared TIP profile.
    replace(tsf / 'Server.cpp', '  if (!RegisterServer() || !RegisterProfiles() || !RegisterCategories()) {',
            '  if (!RegisterServer()\n#ifdef _WIN64\n      || !RegisterProfiles() || !RegisterCategories()\n#endif\n  ) {')
    replace(tsf / 'Server.cpp', '  UnregisterProfiles();\n  UnregisterCategories();',
            '#ifdef _WIN64\n  UnregisterProfiles();\n  UnregisterCategories();\n#endif')

    server = source / 'WeaselServer'
    server_header = server / 'WeaselServerApp.h'
    replace(server_header, '#include <winsparkle.h>\n', '')
    replace(server_header, '#include "WeaselTrayIcon.h"\n', '')
    replace(server_header, '  WeaselTrayIcon tray_icon;\n', '')
    replace(server_header, '  void SetupMenuHandlers();\n\n', '')
    replace_pattern(server_header, r'(?ms)^  static bool check_update\(\).*?^  }\n', '')
    server_main = server / 'WeaselServer.cpp'
    replace(server_main, '#include <winsparkle.h>\n', '')
    replace(server_main, '#include "WeaselService.h"\n', '#include "WeaselServerApp.h"\n')
    replace_pattern(server_main, r'(?ms)^  // restart if already running.*?^  }\n\n  bool check_updates.*?^  }\n', r'''
  weasel::Client client;
  if (client.Connect()) {
    if (quit)
      client.ShutdownServer();
    return 0;
  }
  if (quit)
    return 0;
'''.lstrip('\n'))
    server_project = server / 'WeaselServer.vcxproj'
    for entry in ('WeaselService', 'WeaselTrayIcon'):
        replace(server_project, f'    <ClCompile Include="{entry}.cpp" />\n', '')

    deployer = source / 'WeaselDeployer/WeaselDeployer.vcxproj'
    for entry in ('Configurator', 'DictManagementDialog', 'SwitcherSettingsDialog',
                  'UIStyleSettings', 'UIStyleSettingsDialog'):
        replace(deployer, f'    <ClCompile Include="{entry}.cpp" />\n', '')
    replace(deployer, '<PrecompiledHeader>Use</PrecompiledHeader>',
            '<PrecompiledHeader>Use</PrecompiledHeader>\n      <LanguageStandard>stdcpp17</LanguageStandard>',
            expected=4)

    for project, original_filename, branded in (
        (tsf, 'WeaselTSF', 'ShuangpinIme'),
        (server, 'WeaselServer', 'ShuangpinImeServer'),
        (source / 'WeaselDeployer', 'WeaselDeployer', 'ShuangpinImeDeployer'),
    ):
        resource = project / f'{original_filename}.rc'
        encoding = 'utf-16' if resource.read_bytes().startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig'
        content = resource.read_text(encoding=encoding)
        filename = branded + ('.dll' if project == tsf else '.exe')
        identities = {'InternalName': branded, 'OriginalFilename': filename}
        content = re.sub(r'(?m)^(\s*VALUE "(CompanyName|FileDescription|InternalName|OriginalFilename|ProductName)",[ \t]*)[^\n]+',
                         lambda match: f'{match[1]}"{identities.get(match[2], "双拼互联")}"', content)
        content = content.replace('VALUE "FileVersion", FILE_VERSION', f'VALUE "FileVersion", "{version}.0"')
        content = content.replace('小狼毫', '双拼互联').replace('afxres.h', 'winres.h')
        if project == server:
            content = re.sub(r'(?ms)^\w+ APPCAST\s*\nBEGIN\s*\n.*?^END\n', '', content)
        if project == tsf:
            content = re.sub(r'(?ms)^(IDR_MENU_POPUP\w* MENU)\nBEGIN.*?^END',
                             r'\1\nBEGIN\n    POPUP "双拼互联"\n    BEGIN\n        MENUITEM "双拼互联设置", ID_WEASELTRAY_SETTINGS\n    END\nEND', content)
        resource.write_text(content, encoding=encoding)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--checkout', required=True, type=Path)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--boost', required=True, type=Path)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    git = ['git', '-c', f'safe.directory={args.checkout.resolve().as_posix()}', '-C', str(args.checkout)]
    actual = subprocess.check_output(git + ['rev-parse', 'HEAD'], text=True).strip()
    if actual != WEASEL_REVISION:
        raise RuntimeError(f'Unexpected native upstream revision {actual}')
    archive = subprocess.check_output(git + ['archive', '--format=zip', 'HEAD'])
    with ZipFile(BytesIO(archive)) as zipfile:
        zipfile.extractall(args.source)
    write_overlay(args.source, args.version, args.boost)


if __name__ == '__main__':
    main()
