#pragma once

#include "EditSession.h"

// Apply the pick in the original editor's write session, after the popup's
// mouse handler returns. Reading the IPC response here cannot race a key event.
class LinkCandidatePick final : public CEditSession {
 public:
  LinkCandidatePick(WeaselTSF* service, ITfContext* context, size_t index, BSTR expected)
      : CEditSession(service, context), index_(index), expected_(expected) {}

  STDMETHODIMP DoEditSession(TfEditCookie ec) override {
    auto& service = *_pTextService.p;
    com_ptr<ITfDocumentMgr> active, target;
    CComBSTR current;
    if (!service._pThreadMgr || service._pEditSessionContext.p != _pContext.p ||
        !service._status.composing || FAILED(service._pThreadMgr->GetFocus(&active)) ||
        FAILED(_pContext->GetDocumentMgr(&target)) || !active || target.p != active.p ||
        FAILED(service._cand->GetString(static_cast<UINT>(index_), &current)) ||
        !current.m_str || expected_ != current.m_str)
      return S_FALSE;
    if (!service.m_client.SelectCandidateOnCurrentPage(index_)) return S_FALSE;
    return service.DoEditSession(ec);
  }

 private:
  size_t index_;
  std::wstring expected_;
};

void WeaselTSF::_SelectCandidateOnCurrentPage(size_t index) {
  CComBSTR expected;
  if (!_pEditSessionContext || FAILED(_cand->GetString(static_cast<UINT>(index), &expected)) || !expected.m_str)
    return;
  com_ptr<ITfEditSession> pick;
  pick.Attach(new LinkCandidatePick(this, _pEditSessionContext, index, expected));
  HRESULT result;
  _pEditSessionContext->RequestEditSession(_tfClientId, pick, TF_ES_ASYNC | TF_ES_READWRITE, &result);
}
