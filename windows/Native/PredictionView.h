#pragma once

#include <commctrl.h>
#pragma comment(lib, "comctl32.lib")

static bool LinkInsertAtCaret(ITfContext* context, TfEditCookie ec, const std::wstring& text) {
  com_ptr<ITfInsertAtSelection> insert;
  com_ptr<ITfRange> inserted;
  if (FAILED(context->QueryInterface(&insert)) || !insert ||
      FAILED(insert->InsertTextAtSelection(ec, 0, text.data(), static_cast<LONG>(text.size()), &inserted)))
    return false;
  if (inserted && SUCCEEDED(inserted->Collapse(ec, TF_ANCHOR_END))) {
    TF_SELECTION caret{inserted.p, {TF_AE_NONE, FALSE}};
    context->SetSelection(ec, 1, &caret);
  }
  return true;
}

// Passive suggestions own a candidate window and an anchor, never a document
// composition. Ending them cannot erase text or reset the editor's selection.
void WeaselTSF::_StartLinkPrediction(TfEditCookie ec) {
  if (_linkPredictionContext) return;
  _linkPredictionContext = _pEditSessionContext;
  TF_SELECTION selection{};
  ULONG fetched = 0;
  if (_linkPredictionContext->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &fetched) == S_OK && fetched) {
    _linkPredictionAnchor.Attach(selection.range);
    _linkPredictionAnchor->SetGravity(ec, TF_GRAVITY_BACKWARD, TF_GRAVITY_BACKWARD);
  }
  _StartUI();
  HWND editor = GetFocus();
  if (editor && GetWindowThreadProcessId(editor, nullptr) == GetCurrentThreadId() &&
      SetWindowSubclass(editor, _LinkEditorMessage, reinterpret_cast<UINT_PTR>(this), 0))
    _linkPredictionWindow = editor;
}

void WeaselTSF::_EndLinkPrediction(bool notifyEngine) {
  if (!_linkPredictionContext) return;
  if (_linkPredictionWindow) {
    RemoveWindowSubclass(_linkPredictionWindow, _LinkEditorMessage, reinterpret_cast<UINT_PTR>(this));
    _linkPredictionWindow = nullptr;
  }
  _linkPredictionContext.Release();
  _linkPredictionAnchor.Release();
  if (notifyEngine) {
    m_client.ClearComposition(true);
    _status.composing = false;
  }
  _EndUI();
}

void WeaselTSF::_CheckPredictionCaret(ITfContext* context, TfEditCookie ec) {
  if (_linkPredictionContext.p != context || !_linkPredictionAnchor) return;
  TF_SELECTION selection{};
  ULONG fetched = 0;
  if (context->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &fetched) != S_OK || !fetched) return;
  LONG start = 0, end = 0;
  const bool moved = SUCCEEDED(_linkPredictionAnchor->CompareStart(ec, selection.range, TF_ANCHOR_START, &start)) &&
                     SUCCEEDED(_linkPredictionAnchor->CompareEnd(ec, selection.range, TF_ANCHOR_END, &end)) &&
                     (start != 0 || end != 0);
  selection.range->Release();
  if (moved) _EndLinkPrediction(true);
}

LRESULT CALLBACK WeaselTSF::_LinkEditorMessage(HWND window, UINT message, WPARAM wParam,
                                               LPARAM lParam, UINT_PTR id, DWORD_PTR) {
  // This is the focused editor HWND only. An explicit click on a candidate is
  // delivered to the separate TSF popup. Always continue the editor's message.
  const bool replacesText = message == WM_SETTEXT || message == EM_REPLACESEL || message == EM_STREAMIN;
  if (message == EM_SETSEL || message == EM_EXSETSEL || replacesText) {
    // Native Edit/RichEdit can change selection without reporting an OnEndEdit,
    // including when their WinForms wrapper selects a range programmatically.
    wchar_t name[160]{};
    GetClassNameW(window, name, ARRAYSIZE(name));
    const bool edit = _wcsicmp(name, L"Edit") == 0 || _wcsnicmp(name, L"RichEdit", 8) == 0 ||
                      _wcsnicmp(name, L"WindowsForms10.EDIT.", 20) == 0 ||
                      _wcsnicmp(name, L"WindowsForms10.RichEdit", 23) == 0;
    if (edit && replacesText) {
      reinterpret_cast<WeaselTSF*>(id)->_EndLinkPrediction(true);
    } else if (edit) {
      com_ptr<WeaselTSF> service(reinterpret_cast<WeaselTSF*>(id));
      com_ptr<ITfContext> original = service->_linkPredictionContext;
      DWORD previousStart = 0, previousEnd = 0, nextStart = 0, nextEnd = 0;
      SendMessageW(window, EM_GETSEL, reinterpret_cast<WPARAM>(&previousStart), reinterpret_cast<LPARAM>(&previousEnd));
      const LRESULT result = DefSubclassProc(window, message, wParam, lParam);
      if (service->_linkPredictionContext.p == original.p && service->_linkPredictionWindow == window) {
        SendMessageW(window, EM_GETSEL, reinterpret_cast<WPARAM>(&nextStart), reinterpret_cast<LPARAM>(&nextEnd));
        if (previousStart != nextStart || previousEnd != nextEnd)
          service->_EndLinkPrediction(true);
      }
      return result;
    }
  }
  switch (message) {
    case WM_LBUTTONDOWN: case WM_RBUTTONDOWN: case WM_MBUTTONDOWN: case WM_XBUTTONDOWN:
#ifdef WM_POINTERDOWN
    case WM_POINTERDOWN:
#endif
    case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL: case WM_CONTEXTMENU:
    case WM_HSCROLL: case WM_VSCROLL: case WM_KILLFOCUS: case WM_NCDESTROY:
    case WM_COPY: case WM_CUT: case WM_PASTE: case WM_CLEAR: case WM_UNDO:
      reinterpret_cast<WeaselTSF*>(id)->_EndLinkPrediction(true);
      break;
  }
  return DefSubclassProc(window, message, wParam, lParam);
}
