"""Exercise the actual packaged Rime engine without installing an IME on the host."""
import argparse
import ctypes as c
import os
import shutil
import tempfile
from pathlib import Path

options = argparse.ArgumentParser(description=__doc__)
options.add_argument("--dll", type=Path, default=Path(".build/weasel-test-native/rime.dll"))
options.add_argument("--shared", type=Path, default=Path(".build/weasel-bin/data"))
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

def run_suite(user):
    shared = args.shared.resolve()
    shutil.copytree(".build/windows-data", user, dirs_exist_ok=True)
    (user / "link_habits").mkdir(exist_ok=True)
    (user / "custom_phrase.txt").write_text(
        "青柚竹筏\tzzqa\t99999\n青柚\tzzqb\t99999\n纸鹤锚点\tzzqc\t99999\n紫瓷圆盘\tzzqd\t99999\n",
        encoding="utf-8")
    traits = Traits(c.sizeof(Traits)-4, str(shared).encode(), str(user).encode(), b"Shuangpin Link",
                    b"shuangpin-link", b"1", b"rime.habits-test", None, 2, b"", None, None)
    api("Setup", None, c.POINTER(Traits))(c.byref(traits))
    api("Initialize", None, c.POINTER(Traits))(c.byref(traits))
    try:
        api("StartMaintenance", I, I)(1)
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
        def key(code):
            return api("ProcessKey", I, Z, I, I)(session, code, 0)
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
        for schema, full in ((b"double_pinyin_mspy", "nihk"), (b"rime_ice", "nihao")):
            assert api("SelectSchema", I, Z, S)(session, schema)
            reset()
            print(schema, "full", type_code(full))
            assert "你好" == menu()[0]
            reset()
            print(schema, "initials", type_code("zg"))
            assert "中国" in menu()
        reset()
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
        reset()
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
        assert menu()[0] == "竹筏", menu()
        assert key(32) and commit() == "竹筏"
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
        print("Passed both schemas, frequency ranking, offline continuation, persistence, editor boundaries and email.")
    finally:
        api("Finalize", None)()

if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="ime-habits-", dir=".build") as temp:
        run_suite(Path(temp).resolve())
