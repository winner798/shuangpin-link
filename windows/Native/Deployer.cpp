#include "stdafx.h"
#include <WeaselConstants.h>
#include <WeaselIPC.h>
#include <WeaselUtility.h>
#include <rime_api.h>
#include <rime_levers_api.h>
#include <shellapi.h>

CAppModule _Module;

namespace {
bool VerifySchemes(RimeApi* rime) {
  RimeSchemaList schemas{};
  if (!rime->get_schema_list(&schemas))
    return false;
  const bool valid = schemas.size == 2 &&
      std::string_view(schemas.list[0].schema_id) == "double_pinyin_mspy" &&
      std::string_view(schemas.list[1].schema_id) == "rime_ice";
  rime->free_schema_list(&schemas);
  return valid;
}

void Initialize(RimeApi* rime) {
  const auto shared_dir = WeaselSharedDataPath().u8string();
  const auto user_dir = WeaselUserDataPath().u8string();
  const auto log_dir = WeaselLogPath().u8string();
  const auto name = wtou8(get_weasel_ime_name());
  RIME_STRUCT(RimeTraits, traits);
  traits.shared_data_dir = shared_dir.c_str();
  traits.user_data_dir = user_dir.c_str();
  traits.prebuilt_data_dir = traits.shared_data_dir;
  traits.log_dir = log_dir.c_str();
  traits.app_name = "rime.shuangpin_link";
  traits.distribution_name = name.c_str();
  traits.distribution_code_name = WEASEL_CODE_NAME;
  traits.distribution_version = WEASEL_VERSION;
  rime->setup(&traits);
  rime->deployer_initialize(nullptr);
}

bool Restore(RimeApi* rime, const std::filesystem::path& file) {
  if (!std::filesystem::is_regular_file(file))
    return false;
  auto* levers = rime->find_module("levers");
  if (!levers || !levers->get_api)
    return false;
  auto* api = reinterpret_cast<RimeLeversApi*>(levers->get_api());
  return api && api->restore_user_dict && api->restore_user_dict(file.u8string().c_str());
}

bool Run(RimeApi* rime, int argc, wchar_t** argv) {
  Initialize(rime);
  const std::wstring_view command(argv[1]);
  if (command == L"/deploy" && argc == 2) {
    if (!rime->deploy() || !rime->deploy_config_file("shuangpin_link.yaml", "config_version"))
      return false;
    return VerifySchemes(rime);
  }
  if (command == L"/sync" && argc == 2) {
    if (!rime->sync_user_data())
      return false;
    rime->join_maintenance_thread();
    return true;
  }
  return command == L"/restore" && argc == 3 && Restore(rime, argv[2]);
}
}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
  int argc = 0;
  auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  if (!argv || argc < 2) {
    if (argv)
      LocalFree(argv);
    return 2;
  }
  const HANDLE mutex = CreateMutexW(nullptr, TRUE, L"ShuangpinLinkDeployerMutex");
  if (!mutex || GetLastError() == ERROR_ALREADY_EXISTS) {
    if (mutex)
      CloseHandle(mutex);
    LocalFree(argv);
    return 3;
  }
  weasel::Client client;
  if (client.Connect())
    client.StartMaintenance();
  bool success = false;
  auto* rime = rime_get_api();
  try {
    std::filesystem::create_directories(WeaselUserDataPath());
    success = Run(rime, argc, argv);
    rime->finalize();
  } catch (...) {
  }
  ReleaseMutex(mutex);
  CloseHandle(mutex);
  if (client.Connect())
    client.EndMaintenance();
  LocalFree(argv);
  return success ? 0 : 1;
}
