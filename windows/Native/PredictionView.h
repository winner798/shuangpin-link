#pragma once

#include <commctrl.h>
#pragma comment(lib, "comctl32.lib")

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
