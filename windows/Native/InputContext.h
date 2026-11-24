#pragma once

#include "EnglishCorrection.h"

// Read the input scope without requesting any text from the focused application.
class LinkEditorScope final : public CEditSession {
 public:
  LinkEditorScope(WeaselTSF* service, ITfContext* context) : CEditSession(service, context) {}

  STDMETHODIMP DoEditSession(TfEditCookie edit) override {
    TF_SELECTION selection{};
    ULONG count = 0;
    com_ptr<ITfRange> range;
    if (_pContext->GetSelection(edit, TF_DEFAULT_SELECTION, 1, &selection, &count) == S_OK &&
        count == 1 && selection.range)
      range.Attach(selection.range);
    else if (FAILED(_pContext->GetStart(edit, &range)) || !range)
      return E_FAIL;
    return link_english::IsPlainText(_pContext, range, edit, true) ? S_OK : S_FALSE;
  }
};

inline bool LinkPrivateContext(WeaselTSF* service, ITfContext* context, TfClientId client) {
  if (!context || link_english::PasswordControl(GetFocus()))
    return true;
  com_ptr<ITfEditSession> edit;
  edit.Attach(new LinkEditorScope(service, context));
  HRESULT result = E_FAIL;
  return FAILED(context->RequestEditSession(client, edit, TF_ES_SYNC | TF_ES_READ, &result)) ||
         result != S_OK;
}
