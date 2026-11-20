#pragma once

#include <cwctype>
#include <inputscope.h>
#include <string_view>

#include "EditSession.h"
#include "LinkEnglishWords.h"

namespace link_english {

inline bool IsLetter(wchar_t c) {
  return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z');
}

inline bool IsPlainText(ITfContext* context, ITfRange* range, TfEditCookie edit) {
  wchar_t name[24]{};
  HWND focus = GetFocus();
  if (focus && GetClassNameW(focus, name, ARRAYSIZE(name)) &&
      (_wcsicmp(name, L"Edit") == 0 || _wcsnicmp(name, L"RichEdit", 8) == 0) &&
      (GetWindowLongPtrW(focus, GWL_STYLE) & ES_PASSWORD))
    return false;

  // GUID_PROP_INPUTSCOPE is published by the Windows SDK. Resolve it without
  // imposing an additional import on the text service.
  const GUID inputScope = {0x1713dd5a, 0x68e7, 0x4a5b,
                          {0x9a, 0xf6, 0x59, 0x2a, 0x59, 0x5c, 0x77, 0x8d}};
  com_ptr<ITfReadOnlyProperty> property;
  if (FAILED(context->GetAppProperty(inputScope, &property)) || !property)
    return true;
  CComVariant value;
  if (property->GetValue(edit, range, &value) != S_OK || value.vt == VT_EMPTY)
    return true;
  if (value.vt != VT_UNKNOWN || !value.punkVal)
    return false;
  com_ptr<ITfInputScope> scope;
  if (FAILED(value.punkVal->QueryInterface(IID_PPV_ARGS(&scope))))
    return false;
  InputScope* kinds = nullptr;
  UINT count = 0;
  if (FAILED(scope->GetInputScopes(&kinds, &count)))
    return false;
  bool text = true;
  for (UINT index = 0; index < count; ++index) {
    if (kinds[index] != IS_DEFAULT && kinds[index] != IS_TEXT && kinds[index] != IS_CHAT)
      text = false;
  }
  CoTaskMemFree(kinds);
  return text;
}

class SpaceEdit final : public CEditSession {
 public:
  SpaceEdit(WeaselTSF* service, ITfContext* context)
      : CEditSession(service, context) {}

  STDMETHODIMP DoEditSession(TfEditCookie edit) override {
    if (CorrectPreviousWord(edit))
      return S_OK;

    // An editor can grant its write lock asynchronously. Deliver the original
    // space even if the caret or spelling changed while the request was queued.
    com_ptr<ITfInsertAtSelection> insert;
    HRESULT result = _pContext->QueryInterface(IID_PPV_ARGS(&insert));
    if (FAILED(result))
      return result;
    return insert->InsertTextAtSelection(edit, TF_IAS_NOQUERY, L" ", 1, nullptr);
  }

 private:
  bool CorrectPreviousWord(TfEditCookie edit) {
    TF_SELECTION selection{};
    ULONG count = 0;
    if (FAILED(_pContext->GetSelection(edit, TF_DEFAULT_SELECTION, 1, &selection, &count)) ||
        count != 1 || !selection.range)
      return false;
    com_ptr<ITfRange> caret;
    caret.Attach(selection.range);
    BOOL empty = FALSE;
    if (FAILED(caret->IsEmpty(edit, &empty)) || !empty ||
        selection.style.fInterimChar || !IsPlainText(_pContext, caret, edit))
      return false;

    com_ptr<ITfRange> range;
    if (FAILED(caret->Clone(&range)))
      return false;
    wchar_t buffer[32]{};
    LONG shift = 0;
    ULONG length = 0;
    if (FAILED(range->ShiftStart(edit, -static_cast<LONG>(ARRAYSIZE(buffer)), &shift, nullptr)) ||
        FAILED(range->GetText(edit, 0, buffer, ARRAYSIZE(buffer), &length)))
      return false;
    ULONG start = length;
    while (start && IsLetter(buffer[start - 1]))
      --start;
    if (length - start < 3 || length - start > 24 ||
        (start && !iswspace(buffer[start - 1])) || (!start && length == ARRAYSIZE(buffer)))
      return false;

    std::wstring_view word(buffer + start, length - start);
    std::wstring lowered(word);
    bool uppercase = true;
    bool titlecase = word.front() >= L'A' && word.front() <= L'Z';
    for (size_t index = 0; index < word.size(); ++index) {
      if (word[index] >= L'A' && word[index] <= L'Z') {
        lowered[index] += L'a' - L'A';
        if (index)
          titlecase = false;
      } else {
        uppercase = false;
      }
    }
    if (!uppercase && !titlecase && lowered != word)
      return false;

    for (const auto& [typo, spelling] : kLinkEnglishWords) {
      if (lowered != typo)
        continue;
      std::wstring replacement(spelling);
      if (uppercase) {
        for (auto& letter : replacement)
          letter -= L'a' - L'A';
      } else if (titlecase) {
        replacement[0] -= L'a' - L'A';
      }
      replacement.push_back(L' ');
      const auto wordLength = static_cast<LONG>(word.size());
      if (FAILED(range->Collapse(edit, TF_ANCHOR_END)) ||
          FAILED(range->ShiftStart(edit, -wordLength, &shift, nullptr)) || shift != -wordLength)
        return false;
      if (FAILED(range->SetText(edit, TF_ST_CORRECTION, replacement.c_str(),
                                static_cast<LONG>(replacement.size()))))
        return false;

      if (SUCCEEDED(range->Collapse(edit, TF_ANCHOR_END))) {
        selection.range = range;
        _pContext->SetSelection(edit, 1, &selection);
      }
      return true;
    }
    return false;
  }
};

inline bool EnabledForSpace(LPARAM data) {
  if ((data & (1L << 30)) ||
      ((GetKeyState(VK_SHIFT) | GetKeyState(VK_CONTROL) | GetKeyState(VK_MENU) |
        GetKeyState(VK_LWIN) | GetKeyState(VK_RWIN)) & 0x8000))
    return false;
  DWORD enabled = 0, size = sizeof(enabled);
  return RegGetValueW(HKEY_CURRENT_USER, L"Software\\ShuangpinLink\\IME", L"EnglishAutocorrect",
                      RRF_RT_REG_DWORD, nullptr, &enabled, &size) == ERROR_SUCCESS && enabled == 1;
}

inline bool HandleSpace(WeaselTSF* service, ITfContext* context, TfClientId id) {
  com_ptr<ITfEditSession> edit;
  edit.Attach(new SpaceEdit(service, context));
  HRESULT result = S_FALSE;
  return SUCCEEDED(context->RequestEditSession(id, edit, TF_ES_ASYNCDONTCARE | TF_ES_READWRITE, &result)) &&
         (result == S_OK || result == TF_S_ASYNC);
}

}  // namespace link_english
