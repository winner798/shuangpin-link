#include "stdafx.h"
#include "Register.h"
#include <WeaselUtility.h>

namespace {
constexpr wchar_t kName[] = L"双拼互联";
constexpr wchar_t kClassPrefix[] = L"Software\\Classes\\CLSID\\";
const GUID kCategories[] = {
    GUID_TFCAT_TIP_KEYBOARD,
    GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT,
    GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT,
    GUID_TFCAT_TIPCAP_UIELEMENTENABLED,
    GUID_TFCAT_TIPCAP_INPUTMODECOMPARTMENT,
    GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER,
};

std::wstring ClassPath() {
  wchar_t id[39]{};
  StringFromGUID2(c_clsidTextService, id, ARRAYSIZE(id));
  return std::wstring(kClassPrefix) + id;
}

std::wstring ModulePath() {
  wchar_t path[32768]{};
  const DWORD length = GetModuleFileNameW(g_hInst, path, ARRAYSIZE(path));
  return length && length < ARRAYSIZE(path) ? std::wstring(path, length)
                                           : std::wstring();
}

bool SetString(HKEY key, const wchar_t* name, const std::wstring& value) {
  return RegSetValueExW(key, name, 0, REG_SZ,
                        reinterpret_cast<const BYTE*>(value.c_str()),
                        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
}
}  // namespace

BOOL RegisterProfiles() {
  CComPtr<ITfInputProcessorProfileMgr> manager;
  const auto filename = ModulePath();
  if (filename.empty() || FAILED(manager.CoCreateInstance(CLSID_TF_InputProcessorProfiles)))
    return FALSE;
  return SUCCEEDED(manager->RegisterProfile(
      c_clsidTextService, TEXTSERVICE_LANGID_HANS, c_guidProfile, kName,
      ARRAYSIZE(kName) - 1, filename.c_str(), static_cast<ULONG>(filename.size()),
      TEXTSERVICE_ICON_INDEX, nullptr, 0, TRUE, 0));
}

void UnregisterProfiles() {
  CComPtr<ITfInputProcessorProfileMgr> manager;
  if (SUCCEEDED(manager.CoCreateInstance(CLSID_TF_InputProcessorProfiles)))
    manager->UnregisterProfile(c_clsidTextService, TEXTSERVICE_LANGID_HANS, c_guidProfile, 0);
}

BOOL RegisterCategories() {
  CComPtr<ITfCategoryMgr> manager;
  if (FAILED(manager.CoCreateInstance(CLSID_TF_CategoryMgr)))
    return FALSE;
  for (const auto& category : kCategories) {
    if (FAILED(manager->RegisterCategory(c_clsidTextService, category, c_clsidTextService)))
      return FALSE;
  }
  return TRUE;
}

void UnregisterCategories() {
  CComPtr<ITfCategoryMgr> manager;
  if (SUCCEEDED(manager.CoCreateInstance(CLSID_TF_CategoryMgr))) {
    for (const auto& category : kCategories)
      manager->UnregisterCategory(c_clsidTextService, category, c_clsidTextService);
  }
}

BOOL RegisterServer() {
  const auto filename = ModulePath();
  if (filename.empty())
    return FALSE;
  const auto key_path = ClassPath();
  HKEY entry = nullptr;
  if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, key_path.c_str(), 0, nullptr, 0,
                      KEY_WRITE, nullptr, &entry, nullptr) != ERROR_SUCCESS)
    return FALSE;
  HKEY server = nullptr;
  const bool success = SetString(entry, nullptr, kName) &&
      RegCreateKeyExW(entry, L"InprocServer32", 0, nullptr, 0, KEY_WRITE,
                      nullptr, &server, nullptr) == ERROR_SUCCESS &&
      SetString(server, nullptr, filename) && SetString(server, L"ThreadingModel", L"Apartment");
  if (server)
    RegCloseKey(server);
  RegCloseKey(entry);
  return success;
}

void UnregisterServer() {
  RegDeleteTreeW(HKEY_LOCAL_MACHINE, ClassPath().c_str());
}
