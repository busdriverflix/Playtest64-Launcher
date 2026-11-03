# The launcher, for this machine (make) and for Windows (make windows, cross built with MinGW).
# No libraries: libc on Linux; the system's own WinHTTP, user32 and the like on Windows.

CC      ?= gcc
CFLAGS  := -std=c11 -O2 -Wall -Wextra -Wpedantic -Wshadow -Wstrict-prototypes
SOURCES := src/main.c src/json.c src/inflate.c src/zip.c src/launcher.c src/http_sink.c

WIN_CC     ?= x86_64-w64-mingw32-gcc
WIN_RC     ?= x86_64-w64-mingw32-windres

all: build/linux/launcher

build/linux/launcher: $(SOURCES) src/ui_terminal.c src/platform/platform_linux.c src/platform/http_posix.c $(wildcard src/*.h)
	@mkdir -p build/linux
	$(CC) $(CFLAGS) -o $@ $(SOURCES) src/ui_terminal.c src/platform/platform_linux.c src/platform/http_posix.c

windows: build/windows/launcher.exe

build/windows/launcher.res: res/launcher.rc res/launcher.manifest
	@mkdir -p build/windows
	$(WIN_RC) -I res -O coff $< $@

build/windows/launcher.exe: $(SOURCES) src/ui_win32.c src/platform/platform_win32.c src/platform/http_winhttp.c build/windows/launcher.res $(wildcard src/*.h)
	$(WIN_CC) $(CFLAGS) -o $@ $(SOURCES) src/ui_win32.c src/platform/platform_win32.c src/platform/http_winhttp.c build/windows/launcher.res \
		-mwindows -static -lwinhttp -lcomctl32 -lshell32 -ladvapi32 -luser32 -lgdi32

tests: build/linux/launcher
	python3 tests/run_tests.py

clean:
	rm -rf build

.PHONY: all windows tests clean
