"""Exercise the actual packaged Rime engine without installing an IME on the host."""
import argparse
import ctypes as c
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

options = argparse.ArgumentParser(description=__doc__)
options.add_argument("--dll", type=Path, default=Path(".build/weasel-test-native/rime.dll"))
options.add_argument("--shared", type=Path, default=Path(".build/weasel-bin/data"))
options.add_argument("--data", type=Path, default=Path(".build/windows-data"), help="Prepared product schemas and dictionaries")
options.add_argument("--memory-phase", choices=("train", "reopen"), help=argparse.SUPPRESS)
options.add_argument("--memory-profile", type=Path, help=argparse.SUPPRESS)
args = options.parse_args()
dll_path = args.dll.resolve()
dll_folder = os.add_dll_directory(str(dll_path.parent))
lib = c.CDLL(str(dll_path))
I, S, P, Z = c.c_int, c.c_char_p, c.c_void_p, c.c_size_t

class Traits(c.Structure):
    _fields_ = [("data_size", I), ("shared",S), ("user",S), ("name",S), ("code",S),
                ("version",S), ("app",S), ("modules",P), ("level",I), ("log",S), ("prebuilt",S), ("staging",S)]

class Composition(c.Structure):
    _fields_ = [("length",I), ("position",I), ("start",I), ("end",I), ("preedit",S)]

class Candidate(c.Structure):
    _fields_ = [("text",S), ("comment",S), ("reserved",P)]

class Menu(c.Structure):
    _fields_ = [("page_size",I), ("page",I), ("last",I), ("highlighted",I), ("num",I),
                ("items",c.POINTER(Candidate)), ("keys",S)]

class Context(c.Structure):
    _fields_ = [("data_size",I), ("composition",Composition), ("menu",Menu), ("preview",S), ("labels",P)]

def api(name, result, *params):
    callback = getattr(lib, "Rime" + name)
    callback.restype, callback.argtypes = result, params
    return callback

class Commit(c.Structure):
    _fields_ = [("size", I), ("text", S)]

def prepare_profile(user):
    shutil.copytree(args.data, user, dirs_exist_ok=True)
    (user / "link_habits").mkdir(exist_ok=True)
    # Exercise the same per-schema switch used by both clients' "pause learning" setting.
    if not args.memory_phase:
        (user / "double_pinyin_mspy.custom.yaml").write_text(
            "patch:\n  translator/enable_user_dict: false\n", encoding="utf-8")
    phrases = (
        "青柚竹筏\tzzqa\t99999\n青柚\tzzqb\t99999\n纸鹤锚点\tzzqc\t99999\n紫瓷圆盘\tzzqd\t99999\n"
        "明天\tzzqe\t99999\n下午\tzzqf\t99999\n新式陶灯\tzzqg\t99999\n流光玻璃\tzzqh\t99999\n"
        "晚些\tzzqi\t99999\n安排\tzzqj\t99999\n晚些安排\tzzqk\t99999\n"
        "春汛\tzzql\t99999\n春讯\tzzql\t99998\n"
        "山麓观测小组今天下午汇报栖息地夜间监测结果\tzzqm\t99999\n"
        "山麓观测小组今天下午\tzzqn\t99999\n")
    (user / "custom_phrase.txt").write_text(phrases, encoding="utf-8")
    if args.memory_phase:
        (user / "custom_phrase_double.txt").write_text(phrases, encoding="utf-8")


def run_suite(user):
    shared = args.shared.resolve()
    if args.memory_phase != "reopen":
        prepare_profile(user)
    traits = Traits(c.sizeof(Traits)-4, str(shared).encode(), str(user).encode(), b"Shuangpin Link",
                    b"shuangpin-link", b"1", b"rime.habits-test", None, 2, b"", None, None)
    api("Setup", None, c.POINTER(Traits))(c.byref(traits))
    api("Initialize", None, c.POINTER(Traits))(c.byref(traits))
    try:
        api("StartMaintenance", I, I)(int(args.memory_phase != "reopen"))
        api("JoinMaintenanceThread", None)()
        session = api("CreateSession", Z)()
        def menu():
            ctx = Context(c.sizeof(Context)-4)
            assert api("GetContext", I, Z, c.POINTER(Context))(session,c.byref(ctx))
            words = [ctx.menu.items[i].text.decode() for i in range(ctx.menu.num)]
            api("FreeContext", I, c.POINTER(Context))(c.byref(ctx))
            return words
        def commit():
            value = Commit(c.sizeof(Commit)-4)
            if not api("GetCommit", I, Z, c.POINTER(Commit))(session,c.byref(value)):
                return ""
            result = value.text.decode()
            api("FreeCommit", I, c.POINTER(Commit))(c.byref(value))
            return result
        def key(code, modifiers=0):
            return api("ProcessKey", I, Z, I, I)(session, code, modifiers)
        def dismiss_prediction(arrow, modifiers=0):
            assert menu(), "Expected post-commit suggestions before moving the caret"
            # False passes the original keystroke to the editor, including modifiers.
            assert not key(arrow, modifiers), "Prediction consumed the cursor movement"
            assert not menu() and not commit(), "Moving the caret selected or retained a prediction"
            assert not key(arrow, modifiers | (1 << 30)), "Prediction consumed the key release"
            assert not menu() and not commit()
            reset()  # A host may refresh composition after moving its selection.
            assert not menu(), "A host selection update resurrected a dismissed prediction"
        def type_code(code):
            for ch in code:
                assert key(ord(ch)), ch
            return menu()
        def choose(word):
            words = menu()
            assert word in words, (word, words)
            assert key(ord("1") + words.index(word))
            assert commit() == word
        def option(name, enabled):
            api("SetOption", None, Z, S, I)(session, name.encode(), enabled)
        def reset():
            api("ClearComposition", None, Z)(session)
        def compare_with_idle(spelling, code, modifiers=0):
            outcomes = []
            for predict in (False, True):
                option("prediction", False)
                reset()
                option("prediction", predict)
                type_code(spelling)
                choose("你好")
                assert bool(menu()) == predict
                handled = key(code, modifiers)
                outcomes.append((handled, commit(), menu()))
                key(code, modifiers | (1 << 30))
            assert outcomes[0] == outcomes[1], (code, modifiers, outcomes)
        def click_candidate(word):
            index = menu().index(word)
            assert api("SelectCandidateOnCurrentPage", I, Z, Z)(session, index)
            assert commit() == word
        def choose_prediction(code, index, modifiers=0):
            expected = menu()[index]
            assert not key(code, modifiers | (1 << 30)) and menu()[index] == expected
            assert key(code, modifiers), (hex(code), expected)
            assert commit() == expected, (hex(code), expected)
            assert not key(code, modifiers | (1 << 30)) and not commit()
        def enter(code, word):
            type_code(code)
            choose(word)
        def new_editor():
            option("link_new_editor", True)
            option("link_new_editor", False)
            reset()
        def verify_identifiers(schema):
            for events, expected in (
                ([(ord("V"), 1), (ord("2"), 0)], "V2"),
                ([(ord("V"), 0), (0xffb2, 16)], "V2"),
                ([(ord(ch), 1 if ch.isalpha() else 0) for ch in "CPU12"], "CPU12"),
                ([(ord("V"), 1), (ord("2"), 0), (0xff08, 0), (ord("3"), 0)], "V3"),
            ):
                new_editor()
                for code, modifiers in events:
                    assert key(code, modifiers) and not commit(), (schema, expected, hex(code))
                    key(code, modifiers | (1 << 30))
                    assert not commit()
                assert key(32) and commit() == expected and not menu(), (schema, expected)
            new_editor()
            assert type_code("R2")[:2] == ["R2", "二"], menu()
            click_candidate("二")
            new_editor()
            assert key(ord("v"))
            if schema == b"rime_ice":
                assert key(ord("2")) and menu()[0] == "二"
                assert key(32) and commit() == "二"
            else:
                # Lowercase v is the Microsoft Shuangpin initial zh.
                second = menu()[1]
                assert key(ord("2")) and commit() == second
            new_editor()
        if args.memory_phase:
            # Each spelling is real input in its respective schema. Distinct anchors
            # keep evidence from the shared habits database independent in this check.
            examples = (
                (b"double_pinyin_mspy", ("m;tm", "明天"), ("xwwu", "下午"), ("klhv", "开会")),
                (b"rime_ice", ("xiazhou", "下周"), ("shangwu", "上午"), ("kaihui", "开会")),
            )
            for schema, head, middle, tail in examples:
                assert api("SelectSchema", I, Z, S)(session, schema)
                expected = middle[1] + tail[1]
                if args.memory_phase == "train":
                    for previous in range(2):
                        new_editor()
                        enter(*head)
                        assert expected not in menu(), (schema, previous, menu())
                        enter(*middle)
                        enter(*tail)
                new_editor()
                enter(*head)
                print(schema, args.memory_phase, head[1], menu(), flush=True)
                assert menu()[0] == expected and middle[1] in menu(), (schema, menu())
                choose_prediction(ord("1"), 0)

            # Four familiar alternatives for a public trigger must fit on page one.
            favorites = (("zzqc", "纸鹤锚点"), ("zzqd", "紫瓷圆盘"),
                         ("zzqg", "新式陶灯"), ("zzqh", "流光玻璃"))
            if args.memory_phase == "train":
                for code, word in favorites:
                    for _ in range(3):
                        new_editor()
                        enter("tianqi", "天气")
                        enter(code, word)
                new_editor()
                enter("zzqm", "山麓观测小组今天下午汇报栖息地夜间监测结果")
                for _ in range(2):
                    new_editor()
                    enter("zzqc", "纸鹤锚点")
                    option("link_private", True)
                    enter("zzqd", "紫瓷圆盘")
                    option("link_private", False)
            new_editor()
            enter("tianqi", "天气")
            print(args.memory_phase, "four personal weather suggestions", menu(), flush=True)
            assert set(menu()[:4]) == {word for _, word in favorites}, menu()
            new_editor()
            enter("zzqn", "山麓观测小组今天下午")
            assert menu()[0] == "汇报栖息地夜间监测结果", menu()
            new_editor()
            enter("zzqc", "纸鹤锚点")
            assert "紫瓷圆盘" not in menu(), menu()
            api("DestroySession", I, Z)(session)
            return
        for schema, full in ((b"double_pinyin_mspy", "nihk"), (b"rime_ice", "nihao")):
            assert api("SelectSchema", I, Z, S)(session, schema)
            reset()
            verify_identifiers(schema)
            print(schema, "full", type_code(full))
            assert "你好" == menu()[0]
            option("incognito_mode", True)  # Exercise the bundled predictor without training the frequency fixture.
            for arrow in (0xff51, 0xff53):
                choose("你好")
                dismiss_prediction(arrow)
                assert type_code(full)[0] == "你好"
            # Left during an unfinished spelling must keep the pending composition.
            key(0xff51)
            assert menu() and not commit()
            reset()
            for code, modifiers in ((32,0), (0xff0d,0), (0xff09,0), (0xff1b,0), (56,0), (48,0),
                                    (0xffb9,16), (49,1), (50,4), (0xffb1,8),
                                    (0xff08,0), (0xffff,0), (0xff52,0), (0xff54,0), (0xff55,0),
                                    (0xff50,0), (0xff57,0), (0xff53,1), (ord("c"),4),
                                    (ord("q"),8), (0xffbe,0), (ord(","),0), (ord("@"),0),
                                    (ord("n"),0)):
                compare_with_idle(full, code, modifiers)
            option("prediction", False)
            reset()
            option("prediction", True)
            type_code(full)
            choose("你好")
            for index in range(len(menu())):
                for zero, modifiers in ((ord("1"), 0), (0xffb1, 16)):
                    new_editor()
                    enter(full, "你好")
                    choose_prediction(zero + index, index, modifiers)
            new_editor()
            enter(full, "你好")
            assert not key(32, 1 << 30) and menu()  # Releasing the commit key keeps the offered words tappable.
            click_candidate(menu()[0])
            type_code(full)
            choose("你好")
            assert not key(0xff1b) and not menu() and not commit()
            type_code(full)
            choose("你好")
            for _ in range(2):  # Holding Space must deliver every repeat to the editor.
                handled = key(32)
                assert ((handled and commit() == " ") or (not handled and not commit())) and not menu()
            assert type_code(full)[0] == "你好"
            reset()
            for edge in (True, False):
                type_code(full)
                choose("你好")
                assert menu()
                option("link_dismiss_prediction", edge)
                assert not menu() and not commit()
                reset()
                assert not menu()
            type_code(full)
            option("link_dismiss_prediction", True)
            assert menu()[0] == "你好" and not commit()
            reset()
            option("incognito_mode", False)
            if schema == b"double_pinyin_mspy":
                new_editor()
                enter("m;tm", "明天")
                enter("xwwu", "下午")
                assert menu()[:3] == ["有空吗", "见", "开会"], menu()
                new_editor()
                enter("m;tm", "明天")
                enter(full, "拟好")
                new_editor()
                enter("m;tm", "明天")
                assert "拟好" not in menu(), menu()
                new_editor()
            print(schema, "initials", type_code("zg"))
            assert "中国" in menu()
            for spelling, word in (("hylj", "会议链接"), ("jtbls", "剪贴板历史")):
                reset()
                assert word in type_code(spelling), (schema, spelling, menu())
        reset()
        # Public continuation works on a fresh profile, including when personal learning is suppressed.
        option("incognito_mode", True)
        for _ in range(2):
            new_editor()
            enter("zzqe", "明天")
            assert menu()[:3] == ["见", "上午", "下午"], menu()
            enter("zzqf", "下午")
            assert menu()[:3] == ["有空吗", "见", "开会"], menu()
        dismiss_prediction(0xff53)
        enter("zzqf", "下午")
        assert menu()[:3] != ["有空吗", "见", "开会"], menu()
        option("link_private", True)
        enter("zzqe", "明天")
        assert not menu(), menu()
        option("link_private", False)
        option("incognito_mode", False)
        # Both paths end with the same two words. The less frequent path needs its third word.
        paths = [(("zzqg", "新式陶灯"), ("zzqc", "纸鹤锚点"), 1),
                 (("zzqh", "流光玻璃"), ("zzqd", "紫瓷圆盘"), 3)]
        for head, tail, repeats in paths:
            for _ in range(repeats):
                new_editor()
                enter(*head)
                enter("zzqi", "晚些")
                enter("zzqj", "安排")
                enter(*tail)
        for middle in [[("zzqi", "晚些"), ("zzqj", "安排")], [("zzqk", "晚些安排")]]:
            new_editor()
            enter("zzqg", "新式陶灯")
            for part in middle:
                enter(*part)
            assert menu()[0] == "纸鹤锚点", menu()
        new_editor()
        enter("zzqi", "晚些")
        enter("zzqj", "安排")
        assert menu()[0] == "紫瓷圆盘", menu()
        # Global frequency favors one homophone. Inside a learned collocation the other wins.
        for _ in range(3):
            new_editor()
            enter("zzql", "春汛")
        for _ in range(2):
            new_editor()
            enter("zzqc", "纸鹤锚点")
            enter("zzql", "春讯")
        new_editor()
        assert type_code("zzql")[0] == "春汛", menu()
        new_editor()
        enter("zzqc", "纸鹤锚点")
        assert type_code("zzql")[0] == "春讯", menu()
        new_editor()
        print("learn from 你好 → 企鹅")
        type_code("nihao")
        choose("你好")
        type_code("qie")
        print("qie",menu())
        choose("企鹅")
        reset()
        type_code("nihao")
        choose("你好")
        print("learned prediction", menu())
        assert menu()[0] == "企鹅"
        dismiss_prediction(0xff51, 1)  # Shift+Left extends the editor's selection.
        type_code("zzqa")
        choose("青柚竹筏")
        type_code("qie")
        choose("企鹅")
        reset()
        type_code("zzqa")
        choose("青柚竹筏")
        assert menu()[0] == "企鹅", menu()
        reset()
        type_code("zzqb")
        choose("青柚")
        assert menu() == ["竹筏"], menu()
        handled = key(ord("2"))
        assert commit() == ("2" if handled else "") and not menu()
        enter("zzqb", "青柚")
        choose_prediction(ord("1"), 0)
        new_editor()
        enter("zzqb", "青柚")
        assert menu()[0] == "竹筏", menu()
        dismiss_prediction(0xff53, 4)  # Ctrl+Right moves a word, even for personal-only suggestions.
        type_code("zzqb")
        choose("青柚")
        click_candidate("竹筏")
        type_code("zzqb")
        choose("青柚")
        assert "竹筏" in menu()
        assert not key(0xff08) and not menu() and not commit()
        reset()
        type_code("zzqc")
        choose("纸鹤锚点")
        option("link_new_editor", True)
        type_code("zzqd")
        choose("紫瓷圆盘")
        option("link_new_editor", False)
        reset()
        type_code("zzqc")
        choose("纸鹤锚点")
        assert "紫瓷圆盘" not in menu()
        option("link_private", True)
        type_code("zzqd")
        choose("紫瓷圆盘")
        type_code("zzqc")
        choose("纸鹤锚点")
        option("link_private", False)
        reset()
        type_code("zzqd")
        choose("紫瓷圆盘")
        assert "纸鹤锚点" not in menu()
        reset()
        for _ in range(3):
            type_code("nihao")
            choose("拟好")
        assert type_code("nihao")[0] == "拟好"
        reset()
        for ascii_mode in (False, True):
            option("ascii_mode", ascii_mode)
            print("email choices", type_code("@"))
            assert "@qq.com" in menu()
            assert key(0xff09)
            assert commit() == "@qq.com"
            type_code("@g")
            assert menu()[:2] == ["@g", "@gmail.com"], menu()
            assert key(0xff09) and commit() == "@gmail.com"
        assert type_code("@16")[0:2] == ["@16", "@163.com"]
        assert key(32)
        assert commit() == "@16 "
        api("DestroySession", I, Z)(session)
        session = api("CreateSession", Z)()
        assert api("SelectSchema", I, Z, S)(session, b"rime_ice")
        option("ascii_mode", False)
        type_code("zzqa")
        choose("青柚竹筏")
        # Further input above also trained other equally frequent successors.
        assert "企鹅" in menu(), menu()
        api("DestroySession", I, Z)(session)
        print("Passed both schemas, public suggestions with learning paused, contextual homophones,"
              " multi-word backoff, persistence, editor boundaries and email.")
    finally:
        api("Finalize", None)()

if __name__ == "__main__":
    if args.memory_phase:
        run_suite(args.memory_profile.resolve())
    else:
        with tempfile.TemporaryDirectory(prefix="ime-habits-", dir=".build") as temp:
            run_suite(Path(temp).resolve())
        # A new native process must reopen the same persisted user database.
        with tempfile.TemporaryDirectory(prefix="ime-memory-", dir=".build") as temp:
            for phase in ("train", "reopen"):
                subprocess.run([sys.executable, "-X", "utf8", "-B", __file__, *sys.argv[1:],
                                "--memory-profile", temp, "--memory-phase", phase], check=True)
