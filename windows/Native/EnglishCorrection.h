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
  SpaceEdit(WeaselTSF* service, ITfContext* context, bool apply)
      : CEditSession(service, context), apply_(apply) {}

  STDMETHODIMP DoEditSession(TfEditCookie edit) override {
    TF_SELECTION selection{};
    ULONG count = 0;
    if (FAILED(_pContext->GetSelection(edit, TF_DEFAULT_SELECTION, 1, &selection, &count)) ||
        count != 1 || !selection.range)
      return S_FALSE;
    com_ptr<ITfRange> caret;
    caret.Attach(selection.range);
    BOOL empty = FALSE;
    if (FAILED(caret->IsEmpty(edit, &empty)) || !empty ||
        selection.style.fInterimChar || !IsPlainText(_pContext, caret, edit))
      return S_FALSE;

    com_ptr<ITfRange> range;
    if (FAILED(caret->Clone(&range)))
      return S_FALSE;
    wchar_t buffer[32]{};
    LONG shift = 0;
    ULONG length = 0;
    if (FAILED(range->ShiftStart(edit, -static_cast<LONG>(ARRAYSIZE(buffer)), &shift, nullptr)) ||
        FAILED(range->GetText(edit, 0, buffer, ARRAYSIZE(buffer), &length)) || length != -shift)
      return S_FALSE;
    ULONG start = length;
    while (start && IsLetter(buffer[start - 1]))
      --start;
    if (length - start < 3 || length - start > 24 ||
        (start && !iswspace(buffer[start - 1])) || (!start && length == ARRAYSIZE(buffer)))
      return S_FALSE;

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
      return S_FALSE;

    for (const auto& [typo, spelling] : kLinkEnglishWords) {
      if (lowered != typo)
        continue;
      if (!apply_)
        return S_OK;
      std::wstring replacement(spelling);
      if (uppercase) {
        for (auto& letter : replacement)
          letter -= L'a' - L'A';
      } else if (titlecase) {
        replacement[0] -= L'a' - L'A';
      }
      replacement.push_back(L' ');
      if (start && (FAILED(range->ShiftStart(edit, static_cast<LONG>(start), &shift, nullptr)) ||
                    shift != static_cast<LONG>(start)))
        return S_FALSE;
      if (FAILED(range->SetText(edit, 0, replacement.c_str(), static_cast<LONG>(replacement.size()))))
        return S_FALSE;

      if (SUCCEEDED(range->Collapse(edit, TF_ANCHOR_END))) {
        selection.range = range;
        _pContext->SetSelection(edit, 1, &selection);
      }
      return S_OK;
    }
    return S_FALSE;
  }

 private:
  const bool apply_;
};

inline bool HandleSpace(WeaselTSF* service, ITfContext* context, TfClientId id,
                        WPARAM key, LPARAM data, bool apply) {
  if (key != VK_SPACE || (data & (1L << 30)) ||
      ((GetKeyState(VK_SHIFT) | GetKeyState(VK_CONTROL) | GetKeyState(VK_MENU) |
        GetKeyState(VK_LWIN) | GetKeyState(VK_RWIN)) & 0x8000))
    return false;
  DWORD enabled = 0, size = sizeof(enabled);
  if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\ShuangpinLink\\IME", L"EnglishAutocorrect",
                   RRF_RT_REG_DWORD, nullptr, &enabled, &size) != ERROR_SUCCESS || enabled != 1)
    return false;
  com_ptr<ITfEditSession> edit;
  edit.Attach(new SpaceEdit(service, context, apply));
  HRESULT result = S_FALSE;
  return SUCCEEDED(context->RequestEditSession(id, edit, TF_ES_SYNC | (apply ? TF_ES_READWRITE : TF_ES_READ), &result)) &&
         result == S_OK;
}

}  // namespace link_english
