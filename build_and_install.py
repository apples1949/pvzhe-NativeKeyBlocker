# -*- coding: utf-8 -*-
"""屏蔽原生热键：编译 → 打包 → 装机（一条龙）。

★ 关键经验（2026-09-29 实测踩坑，与其它 Mod 相同）★
  · dotnet build 必须显式传 -p:GodotRefDir=...，否则引用解析不到；
  · TEMP/TMP/DOTNET_CLI_HOME/NUGET_PACKAGES 全部重定向到工作区内；
  · 先 taskkill /F /IM dotnet.exe 清残留进程。

用法：python build_and_install.py [--install]
      不带 --install 只编译；带则继续打包 + 装机 + 清解包缓存。
"""
import os
import shutil
import subprocess
import sys
# --- 本机路径适配（自动注入；换机器只改 mods/_modenv.py）---
import os as _os
import sys as _sys

_MROOT = _os.path.dirname(_os.path.abspath(__file__))
while not _os.path.isfile(_os.path.join(_MROOT, "_modenv.py")):
    _p = _os.path.dirname(_MROOT)
    if _p == _MROOT:
        break
    _MROOT = _p
if _MROOT not in _sys.path:
    _sys.path.insert(0, _MROOT)
from _modenv import (  # noqa: E402
    MODS_ROOT, WORKSPACE, REF_DIR, GAME, UNPACK, UD, MODS_DIR, CACHE,
    PY, DOTNET, DOTNET_ROOT, require_ref_dir, ensure_mods, describe,
)
# --- 适配块结束 ---

ROOT = MODS_ROOT
DOTNET = DOTNET
BASE = _os.path.dirname(_os.path.abspath(__file__))   # ← 本 Mod 自己的目录
SRC = os.path.join(BASE, "runtime_src")
PY = PY
LOG = os.path.join(SRC, "build_last.log")

# 程序集身份名（= csproj 的 <AssemblyName>）；打包时要改名为 ModAssembly.dll
ASSEMBLY_NAME = "JTYNativeKeyBlocker"

HOME = os.path.join(WORKSPACE, ".cache", "dotnet_home")
TMPD = os.path.join(HOME, "tmp")
NUGET = os.path.join(WORKSPACE, ".cache", "nuget")

UD = UD
MODS = os.path.join(UD, "Mods")
CACHE = os.path.join(UD, "ModsCache")

lines = []


def log(s=""):
    lines.append(str(s))


def run_compile():
    for d in (HOME, TMPD, NUGET):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT
    env["DOTNET_CLI_HOME"] = HOME
    env["TEMP"] = TMPD
    env["TMP"] = TMPD
    env["TMPDIR"] = TMPD
    env["NUGET_PACKAGES"] = NUGET
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    _csproj = next((f for f in sorted(os.listdir(SRC)) if f.endswith(".csproj")), None)
    if not _csproj:
        log("[compile] %s 下找不到 .csproj" % SRC)
        return False
    cmd = [DOTNET, "build", os.path.join(SRC, _csproj),
           "-c", "Release",
           "-p:GodotRefDir=" + require_ref_dir(),
           "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "-v:q", "-nologo"]
    if not os.path.isfile(os.path.join(SRC, "obj", "project.assets.json")):
        log("[compile] 无 obj/project.assets.json，先 restore")
    p = subprocess.run(cmd, cwd=SRC, env=env, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=300)
    log("[compile] RC=%d" % p.returncode)
    if p.stdout:
        log(p.stdout.strip())
    if p.stderr:
        log(p.stderr.strip())
    if p.returncode != 0:
        return False

    # ★ 编译产物是 runtime_src/bin/Release/<程序集身份名>.dll；
    #   包内必须是 Runtime/ModAssembly.dll。这一步必须做——否则打出来的包还是旧 DLL。
    import shutil as _sh
    got = os.path.join(SRC, "bin", "Release", ASSEMBLY_NAME + ".dll")
    if not os.path.isfile(got):
        log("[copy] 找不到编译产物 %s" % got)
        return False
    rtdir = os.path.join(BASE, "Runtime")
    os.makedirs(rtdir, exist_ok=True)
    dst = os.path.join(rtdir, "ModAssembly.dll")
    new_bytes = open(got, "rb").read()
    if os.path.isfile(dst) and open(dst, "rb").read() == new_bytes:
        log("[copy] Runtime/ModAssembly.dll 未变（%d B）" % len(new_bytes))
    else:
        _sh.copyfile(got, dst)
        log("[copy] %s -> Runtime/ModAssembly.dll（%d B）" % (
            os.path.basename(got), len(new_bytes)))
    return True


def main():
    # 清残留 dotnet（否则锁 obj/ 导致卡死）
    try:
        subprocess.run(["taskkill", "/F", "/IM", "dotnet.exe"],
                       capture_output=True, timeout=30)
    except Exception:
        pass

    if not run_compile():
        log("编译失败，终止。")
        return 1
    log("[compile] OK")

    if "--install" not in sys.argv:
        return 0

    p = subprocess.run([PY, os.path.join(BASE, "build_pmod.py")], capture_output=True,
                       text=True, encoding="utf-8", errors="replace", timeout=180)
    log("[package] RC=%d" % p.returncode)
    log((p.stdout or "").strip())

    dist = os.path.join(BASE, "dist", "NativeKeyBlocker.pmod")
    if p.returncode != 0 or not os.path.isfile(dist):
        log("打包失败，终止。")
        return 1

    dst = os.path.join(MODS, "NativeKeyBlocker.pmod")
    shutil.copyfile(dist, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))

    # 清本 Mod 的解包缓存（下次启动重新解包，确保新包生效）
    if os.path.isdir(CACHE):
        import time
        ts = time.strftime("%H%M%S")
        for name in os.listdir(CACHE):
            if name == "NativeKeyBlocker":
                src = os.path.join(CACHE, name)
                dstc = os.path.join(CACHE, name + ".bak_" + ts)
                i = 1
                while os.path.exists(dstc):
                    i += 1
                    dstc = os.path.join(CACHE, "%s.bak_%s_%d" % (name, ts, i))
                try:
                    r = subprocess.run(["cmd", "/c", "move", src, dstc],
                                       capture_output=True, errors="replace",
                                       encoding="gbk", timeout=60)
                    log("[cache] move rc=%d %s" % (
                        r.returncode,
                        ((r.stdout or "") + (r.stderr or "")).strip().replace("\n", " ")))
                except Exception as ex:
                    log("[cache] move 失败（已忽略）：%r" % ex)
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    with open(LOG, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    try:
        print("\n".join(lines))
    except UnicodeEncodeError:
        # Windows GBK 控制台打不出某些字符时降级输出（构建本身已完成）
        import sys as _s
        _s.stdout.buffer.write("\n".join(lines).encode("utf-8", "replace"))
    sys.exit(rc)
