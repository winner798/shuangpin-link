#pragma once

#include <cstdio>
#include <cwctype>
#include <inputscope.h>
#include <richedit.h>
#include <string_view>

#include "EditSession.h"
#include "LinkEnglishWords.h"

namespace link_english {

inline void Trace(const char* step, HRESULT code = S_OK, long value = 0) {
  wchar_t enabled[2]{};
  if (GetEnvironmentVariableW(L"SHUANGPIN_LINK_INPUT_TEST_TRACE", enabled, ARRAYSIZE(enabled)) != 1 ||
      enabled[0] != L'1')
    return;
  char message[144]{};
  const int length = sprintf_s(message, "English correction: %s (%08lx, %ld)\n", step,
                               static_cast<unsigned long>(code), value);
  DWORD written = 0;
  if (length > 0)
    WriteFile(GetStdHandle(STD_ERROR_HANDLE), message, static_cast<DWORD>(length), &written, nullptr);
}

inline bool IsLetter(wchar_t c) {
  return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z');
}

inline std::wstring_view WordAtEnd(std::wstring_view text, bool atDocumentStart) {
  size_t start = text.size();
  while (start && IsLetter(text[start - 1]))
    --start;
  if (text.size() - start < 3 || text.size() - start > 24 ||
      (start ? !iswspace(text[start - 1]) : !atDocumentStart))
    return {};
  return text.substr(start);
}

inline std::wstring CorrectedWord(std::wstring_view word) {
  if (word.size() < 3 || word.size() > 24)
    return {};
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
    return {};
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
    return replacement;
  }
  return {};
}

inline bool PasswordControl(HWND focus) {
  wchar_t name[24]{};
  return focus && GetClassNameW(focus, name, ARRAYSIZE(name)) &&
      (_wcsicmp(name, L"Edit") == 0 || _wcsnicmp(name, L"RichEdit", 8) == 0) &&
      (GetWindowLongPtrW(focus, GWL_STYLE) & ES_PASSWORD);
}

inline bool IsPlainText(ITfContext* context, ITfRange* range, TfEditCookie edit,
                        bool forLearning = false) {
  if (PasswordControl(GetFocus()))
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
  for (UINT index = 0; index < count && text; ++index) {
    const auto kind = kinds[index];
    if (forLearning) {
      text = kind != IS_PRIVATE && kind != IS_PASSWORD && kind != IS_NUMERIC_PASSWORD &&
             kind != IS_NUMERIC_PIN && kind != IS_ALPHANUMERIC_PIN && kind != IS_ALPHANUMERIC_PIN_SET;
    } else {
      text = kind == IS_DEFAULT || kind == IS_TEXT || kind == IS_CHAT;
    }
  }
  CoTaskMemFree(kinds);
  return text;
}

inline void CorrectCompositionCommit(ITfContext* context, TfEditCookie edit, std::wstring& commit) {
  if (!context || commit.empty())
    return;
  std::wstring_view word(commit);
  if (word.back() == L' ')
    word.remove_suffix(1);
  auto replacement = CorrectedWord(word);
  if (replacement.empty())
    return;
  TF_SELECTION selection{};
  ULONG count = 0;
  if (FAILED(context->GetSelection(edit, TF_DEFAULT_SELECTION, 1, &selection, &count)) ||
      count != 1 || !selection.range)
    return;
  com_ptr<ITfRange> caret;
  caret.Attach(selection.range);
  if (IsPlainText(context, caret, edit)) {
    replacement.push_back(L' ');
    commit = std::move(replacement);
  }
}

inline bool CorrectInRichEdit() {
  HWND focus = GetFocus();
  wchar_t name[128]{};
  constexpr wchar_t wrappedRichEdit[] = L"WindowsForms10.RichEdit";
  DWORD process = 0;
  if (!focus || !GetWindowThreadProcessId(focus, &process) || process != GetCurrentProcessId() ||
      !GetClassNameW(focus, name, ARRAYSIZE(name)) ||
      (_wcsnicmp(name, L"RichEdit", 8) != 0 &&
       _wcsnicmp(name, wrappedRichEdit, ARRAYSIZE(wrappedRichEdit) - 1) != 0) ||
      (GetWindowLongPtrW(focus, GWL_STYLE) & (ES_PASSWORD | ES_READONLY)) ||
      SendMessageW(focus, EM_GETPASSWORDCHAR, 0, 0) != 0) {
    Trace("native editor excluded");
    return false;
  }

  CHARRANGE selection{};
  SendMessageW(focus, EM_EXGETSEL, 0, reinterpret_cast<LPARAM>(&selection));
  if (selection.cpMin != selection.cpMax || selection.cpMin <= 0)
    return false;
  wchar_t buffer[33]{};
  const LONG start = selection.cpMin > 32 ? selection.cpMin - 32 : 0;
  TEXTRANGEW range{{start, selection.cpMin}, buffer};
  const LRESULT length = SendMessageW(focus, EM_GETTEXTRANGE, 0, reinterpret_cast<LPARAM>(&range));
  Trace("native range length", S_OK, static_cast<long>(length));
  if (length <= 0 || length > 32)
    return false;
  auto word = WordAtEnd(std::wstring_view(buffer, static_cast<size_t>(length)), start == 0);
  auto replacement = CorrectedWord(word);
  if (replacement.empty() || GetFocus() != focus)
    return false;

  replacement.push_back(L' ');
  CHARRANGE wordRange{selection.cpMin - static_cast<LONG>(word.size()), selection.cpMin};
  SendMessageW(focus, EM_EXSETSEL, 0, reinterpret_cast<LPARAM>(&wordRange));
  if (GetFocus() != focus) {
    SendMessageW(focus, EM_EXSETSEL, 0, reinterpret_cast<LPARAM>(&selection));
    return false;
  }
  SendMessageW(focus, EM_REPLACESEL, TRUE, reinterpret_cast<LPARAM>(replacement.c_str()));
  return true;
}

class SpaceEdit final : public CEditSession {
 public:
  SpaceEdit(WeaselTSF* service, ITfContext* context)
      : CEditSession(service, context) {}

  STDMETHODIMP DoEditSession(TfEditCookie edit) override {
    return CorrectPreviousWord(edit) ? S_OK : S_FALSE;
  }
  bool UseRichEditFallback() const { return emptyStore_; }

 private:
  bool emptyStore_ = false;

  bool CorrectPreviousWord(TfEditCookie edit) {
    TF_SELECTION selection{};
    ULONG count = 0;
    HRESULT result = _pContext->GetSelection(edit, TF_DEFAULT_SELECTION, 1, &selection, &count);
    Trace("get selection", result, count);
    if (FAILED(result) || count != 1 || !selection.range)
      return false;
    com_ptr<ITfRange> caret;
    caret.Attach(selection.range);
    BOOL empty = FALSE;
    result = caret->IsEmpty(edit, &empty);
    if (FAILED(result) || !empty || selection.style.fInterimChar ||
        !IsPlainText(_pContext, caret, edit)) {
      Trace("excluded text scope or selection", result, empty);
      return false;
    }

    com_ptr<ITfRange> range;
    if (FAILED(caret->Clone(&range)))
      return false;
    wchar_t buffer[32]{};
    LONG shift = 0;
    ULONG length = 0;
    result = range->ShiftStart(edit, -static_cast<LONG>(ARRAYSIZE(buffer)), &shift, nullptr);
    Trace("extend range to left", result, shift);
    if (FAILED(result))
      return false;
    result = range->GetText(edit, 0, buffer, ARRAYSIZE(buffer), &length);
    Trace("read range", result, length);
    if (FAILED(result))
      return false;
    if (!length) {
      emptyStore_ = true;
      return false;
    }

    auto word = WordAtEnd(std::wstring_view(buffer, length), length < ARRAYSIZE(buffer));
    auto replacement = CorrectedWord(word);
    if (replacement.empty())
      return false;

    replacement.push_back(L' ');
    const auto wordLength = static_cast<LONG>(word.size());
    if (FAILED(range->Collapse(edit, TF_ANCHOR_END)) ||
        FAILED(range->ShiftStart(edit, -wordLength, &shift, nullptr)) || shift != -wordLength)
      return false;
    result = range->SetText(edit, TF_ST_CORRECTION, replacement.c_str(),
                            static_cast<LONG>(replacement.size()));
    Trace("replace word", result);
    if (FAILED(result))
      return false;

    if (SUCCEEDED(range->Collapse(edit, TF_ANCHOR_END))) {
      selection.range = range;
      _pContext->SetSelection(edit, 1, &selection);
    }
    return true;
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
  auto* session = new SpaceEdit(service, context);
  edit.Attach(session);
  HRESULT result = S_FALSE;
  const HRESULT request = context->RequestEditSession(id, edit, TF_ES_SYNC | TF_ES_READWRITE, &result);
  Trace("request edit", request, result);
  return SUCCEEDED(request) && (result == S_OK || (result == S_FALSE &&
         session->UseRichEditFallback() && CorrectInRichEdit()));
}

}  // namespace link_english
