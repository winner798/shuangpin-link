#include "stdafx.h"
#include "WeaselServerApp.h"

WeaselServerApp::WeaselServerApp()
    : m_handler(std::make_unique<RimeWithWeaselHandler>(&m_ui)) {
  m_server.SetRequestHandler(m_handler.get());
}

WeaselServerApp::~WeaselServerApp() = default;

int WeaselServerApp::Run() {
  if (!m_server.Start())
    return 1;
  m_ui.Create(m_server.GetHWnd());
  m_handler->Initialize();
  const int result = m_server.Run();
  m_handler->Finalize();
  m_ui.Destroy();
  return result;
}
