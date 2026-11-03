/*
	The interface the person uses: a window on Windows (ui_win32.c), text in the terminal on Linux
	(ui_terminal.c). Both only call into launcher.h.
*/

#ifndef UI_H
#define UI_H

#include "launcher.h"

// Runs until the person is done; the return value is the program's exit code
s32 ui_run(Launcher* launcher);

#endif // UI_H
