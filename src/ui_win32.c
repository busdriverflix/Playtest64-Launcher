/*
	The launcher's window. Two pages in one window: signing in, and the builds (a list to pick from, an
	install-or-play button, a progress bar). Everything that talks to the server runs on a worker thread
	and reports back with a message, so the window never freezes while a build comes down.
*/

#include "ui.h"
#include "platform.h"

#include <windows.h>
#include <commctrl.h>

#include <stdio.h>
#include <string.h>

/* Defines */

#define WINDOW_CLASS "Playtest64LauncherWindow"
#define WINDOW_WIDTH 456
#define WINDOW_HEIGHT 340

#define WM_JOB_DONE (WM_APP + 1)			// a worker is finished: lParam is its Job
#define WM_JOB_PROGRESS (WM_APP + 2)		// wParam is the percent of a download

enum ControlId
{
	ID_EMAIL = 100,
	ID_PASSWORD,
	ID_SIGN_IN,
	ID_BUILDS,
	ID_RELOAD,
	ID_NOTES,
	ID_ACTION,
	ID_PROGRESS,
	ID_AUTOSTART,
	ID_SIGN_OUT,
	ID_STATUS,
	ID_LOGIN_LABELS,						// the labels of the sign-in page, one id from here on each
};

/* Types */

typedef enum JobKind
{
	JOB_LOGIN,
	JOB_REFRESH,
	JOB_INSTALL,
} JobKind;

typedef struct Job
{
	JobKind kind;
	char email[128];
	char password[256];
	u32 build_index;
	LauncherResult result;
	char error[LAUNCHER_ERROR];
	HWND window;
} Job;

typedef struct Ui
{
	Launcher* launcher;
	HWND window;
	HFONT font;
	bool busy;
	bool main_page;
	int last_percent;

	HWND sign_in_controls[6];
	HWND main_controls[8];
} Ui;

/* Variables */

static Ui ui;

/* Functions */

static HWND make(const char* class_name, const char* text, DWORD style, int x, int y, int w, int h, int id)
{
	HWND control = CreateWindowExA(0, class_name, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h, ui.window, (HMENU)(INT_PTR)id,
		GetModuleHandleA(NULL), NULL);

	SendMessageA(control, WM_SETFONT, (WPARAM)ui.font, TRUE);

	return control;
}

static void set_status(const char* text)
{
	SetDlgItemTextA(ui.window, ID_STATUS, text);
}

static void show_page(const bool main_page)
{
	ui.main_page = main_page;

	for (u32 i = 0; i < ARRAY_COUNT(ui.sign_in_controls); i++)
		ShowWindow(ui.sign_in_controls[i], main_page ? SW_HIDE : SW_SHOW);

	for (u32 i = 0; i < ARRAY_COUNT(ui.main_controls); i++)
		ShowWindow(ui.main_controls[i], main_page ? SW_SHOW : SW_HIDE);
}

// What the button under the list says and whether it can be pressed, for the build picked
static void update_selection(void)
{
	Launcher* launcher = ui.launcher;
	LRESULT picked = SendDlgItemMessageA(ui.window, ID_BUILDS, CB_GETCURSEL, 0, 0);
	bool have = picked != CB_ERR && (u32)picked < launcher->build_count;

	EnableWindow(GetDlgItem(ui.window, ID_ACTION), have && !ui.busy);
	SetDlgItemTextA(ui.window, ID_NOTES, have ? launcher->builds[picked].notes : "");

	if (have)
		SetDlgItemTextA(ui.window, ID_ACTION, launcher_is_installed(launcher, &launcher->builds[picked]) ? "Spielen" : "Herunterladen und installieren");
}

static void fill_builds(void)
{
	Launcher* launcher = ui.launcher;

	SendDlgItemMessageA(ui.window, ID_BUILDS, CB_RESETCONTENT, 0, 0);

	for (u32 i = 0; i < launcher->build_count; i++)
	{
		char label[96];
		snprintf(label, sizeof(label), "%s%s", launcher->builds[i].version, launcher_is_installed(launcher, &launcher->builds[i]) ? "  (installiert)" : "");
		SendDlgItemMessageA(ui.window, ID_BUILDS, CB_ADDSTRING, 0, (LPARAM)label);
	}

	SendDlgItemMessageA(ui.window, ID_BUILDS, CB_SETCURSEL, 0, 0);
	update_selection();
}

static void set_busy(const bool busy)
{
	ui.busy = busy;

	for (u32 id = ID_SIGN_IN; id <= ID_SIGN_OUT; id++)
	{
		HWND control = GetDlgItem(ui.window, (int)id);

		if (control != NULL && id != ID_NOTES && id != ID_PROGRESS)
			EnableWindow(control, !busy);
	}

	EnableWindow(GetDlgItem(ui.window, ID_EMAIL), !busy);
	EnableWindow(GetDlgItem(ui.window, ID_PASSWORD), !busy);

	if (!busy)
		update_selection();
}

static bool report_progress(u64 received, u64 total, void* user)
{
	Job* job = (Job*)user;

	if (total == 0)
		return true;

	int percent = (int)(received * 100 / total);

	// only when the number changes: a message per read would drown the window
	if (percent != ui.last_percent)
	{
		ui.last_percent = percent;
		PostMessageA(job->window, WM_JOB_PROGRESS, (WPARAM)percent, 0);
	}

	return true;
}

static DWORD WINAPI run_job(LPVOID parameter)
{
	Job* job = (Job*)parameter;
	Launcher* launcher = ui.launcher;

	job->error[0] = '\0';

	switch (job->kind)
	{
	case JOB_LOGIN:
		job->result = launcher_login(launcher, job->email, job->password, job->error, sizeof(job->error));
		break;
	case JOB_REFRESH:
		job->result = launcher_refresh(launcher, job->error, sizeof(job->error));
		break;
	case JOB_INSTALL:
		job->result = launcher_install(launcher, &launcher->builds[job->build_index], report_progress, job, job->error, sizeof(job->error));
		break;
	}

	PostMessageA(job->window, WM_JOB_DONE, 0, (LPARAM)job);

	return 0;
}

static void start_job(const JobKind kind, const u32 build_index)
{
	Job* job = (Job*)calloc(1, sizeof(Job));

	if (job == NULL)
		return;

	job->kind = kind;
	job->build_index = build_index;
	job->window = ui.window;

	if (kind == JOB_LOGIN)
	{
		GetDlgItemTextA(ui.window, ID_EMAIL, job->email, sizeof(job->email));
		GetDlgItemTextA(ui.window, ID_PASSWORD, job->password, sizeof(job->password));
	}

	ui.last_percent = -1;
	SendDlgItemMessageA(ui.window, ID_PROGRESS, PBM_SETPOS, 0, 0);
	set_status(kind == JOB_LOGIN ? "Melde an ..." : kind == JOB_REFRESH ? "Lade die Liste ..." : "Lade herunter ...");
	set_busy(true);

	HANDLE thread = CreateThread(NULL, 0, run_job, job, 0, NULL);

	if (thread == NULL)
	{
		set_busy(false);
		set_status("Der Vorgang lässt sich nicht starten.");
		free(job);
		return;
	}

	CloseHandle(thread);
}

static void finish_job(Job* job)
{
	Launcher* launcher = ui.launcher;

	set_busy(false);

	switch (job->kind)
	{
	case JOB_LOGIN:
		if (job->result == LAUNCHER_OK)
		{
			SetDlgItemTextA(ui.window, ID_PASSWORD, "");		// the password is not kept in the window
			start_job(JOB_REFRESH, 0);
		}
		else
		{
			set_status(job->error);
		}
		break;

	case JOB_REFRESH:
		if (job->result == LAUNCHER_SIGNED_OUT)
		{
			show_page(false);
			set_status(job->error);
		}
		else
		{
			show_page(true);
			fill_builds();

			if (job->result == LAUNCHER_OK)
			{
				set_status(launcher->build_count == 0 ? "Für Windows gibt es noch keinen Build." : "");

				// everything in the list has now been seen, so the background check does not announce it again
				if (launcher->build_count > 0)
				{
					launcher->announced_build = launcher->builds[0].build_id;
					launcher_save(launcher);
				}
			}
			else
			{
				set_status(job->error);
			}
		}
		break;

	case JOB_INSTALL:
		if (job->result == LAUNCHER_OK)
		{
			SendDlgItemMessageA(ui.window, ID_PROGRESS, PBM_SETPOS, 100, 0);
			set_status("Installiert.");
			LRESULT picked = SendDlgItemMessageA(ui.window, ID_BUILDS, CB_GETCURSEL, 0, 0);
			fill_builds();
			SendDlgItemMessageA(ui.window, ID_BUILDS, CB_SETCURSEL, (WPARAM)picked, 0);
			update_selection();
		}
		else
		{
			SendDlgItemMessageA(ui.window, ID_PROGRESS, PBM_SETPOS, 0, 0);

			if (job->result == LAUNCHER_SIGNED_OUT)
				show_page(false);

			set_status(job->error);
		}
		break;
	}

	free(job);
}

static void press_action(void)
{
	Launcher* launcher = ui.launcher;
	LRESULT picked = SendDlgItemMessageA(ui.window, ID_BUILDS, CB_GETCURSEL, 0, 0);

	if (picked == CB_ERR || (u32)picked >= launcher->build_count)
		return;

	const LauncherBuild* build = &launcher->builds[picked];

	if (!launcher_is_installed(launcher, build))
	{
		start_job(JOB_INSTALL, (u32)picked);
		return;
	}

	char error[LAUNCHER_ERROR];

	if (launcher_play(launcher, build, error, sizeof(error)))
		PostMessageA(ui.window, WM_CLOSE, 0, 0);
	else
		set_status(error);
}

static void create_controls(void)
{
	NONCLIENTMETRICSA metrics;

	memset(&metrics, 0, sizeof(metrics));
	metrics.cbSize = sizeof(metrics);
	SystemParametersInfoA(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0);
	ui.font = CreateFontIndirectA(&metrics.lfMessageFont);

	// the sign-in page
	ui.sign_in_controls[0] = make("STATIC", "Mit deinem Playtest-Konto anmelden (dieselben Daten wie auf der Webseite):", 0, 16, 16, 420, 34, ID_LOGIN_LABELS);
	ui.sign_in_controls[1] = make("STATIC", "E-Mail", 0, 16, 58, 200, 18, ID_LOGIN_LABELS + 1);
	ui.sign_in_controls[2] = make("EDIT", ui.launcher->email, WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL, 16, 78, 420, 24, ID_EMAIL);
	ui.sign_in_controls[3] = make("STATIC", "Passwort", 0, 16, 112, 200, 18, ID_LOGIN_LABELS + 2);
	ui.sign_in_controls[4] = make("EDIT", "", WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL | ES_PASSWORD, 16, 132, 420, 24, ID_PASSWORD);
	ui.sign_in_controls[5] = make("BUTTON", "Anmelden", WS_TABSTOP | BS_DEFPUSHBUTTON, 16, 172, 120, 30, ID_SIGN_IN);

	// the builds page
	ui.main_controls[0] = make("STATIC", "Build", 0, 16, 16, 200, 18, ID_LOGIN_LABELS + 3);
	ui.main_controls[1] = make("COMBOBOX", "", WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL, 16, 36, 330, 200, ID_BUILDS);
	ui.main_controls[2] = make("BUTTON", "Neu laden", WS_TABSTOP, 356, 35, 80, 24, ID_RELOAD);
	ui.main_controls[3] = make("EDIT", "", WS_BORDER | ES_MULTILINE | ES_READONLY | WS_VSCROLL, 16, 70, 420, 76, ID_NOTES);
	ui.main_controls[4] = make("BUTTON", "Herunterladen und installieren", WS_TABSTOP | BS_DEFPUSHBUTTON, 16, 158, 420, 36, ID_ACTION);
	ui.main_controls[5] = make(PROGRESS_CLASSA, "", PBS_SMOOTH, 16, 204, 420, 16, ID_PROGRESS);
	ui.main_controls[6] = make("BUTTON", "Nach Updates suchen, wenn ich mich anmelde", WS_TABSTOP | BS_AUTOCHECKBOX, 16, 262, 330, 20, ID_AUTOSTART);
	ui.main_controls[7] = make("BUTTON", "Abmelden", WS_TABSTOP, 356, 258, 80, 26, ID_SIGN_OUT);

	make("STATIC", "", SS_LEFT, 16, 230, 420, 20, ID_STATUS);

	SendDlgItemMessageA(ui.window, ID_AUTOSTART, BM_SETCHECK, platform_autostart_get() ? BST_CHECKED : BST_UNCHECKED, 0);
}

static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
	switch (message)
	{
	case WM_CREATE:
		ui.window = window;
		create_controls();
		show_page(false);

		if (launcher_signed_in(ui.launcher))
			start_job(JOB_REFRESH, 0);
		else
			SetFocus(GetDlgItem(window, ID_EMAIL));

		return 0;

	case WM_COMMAND:
		if (ui.busy)
			return 0;

		switch (LOWORD(wparam))
		{
		case ID_SIGN_IN:
			start_job(JOB_LOGIN, 0);
			break;
		case ID_RELOAD:
			start_job(JOB_REFRESH, 0);
			break;
		case ID_ACTION:
			press_action();
			break;
		case ID_BUILDS:
			if (HIWORD(wparam) == CBN_SELCHANGE)
				update_selection();
			break;
		case ID_AUTOSTART:
		{
			bool wanted = SendDlgItemMessageA(window, ID_AUTOSTART, BM_GETCHECK, 0, 0) == BST_CHECKED;

			if (!platform_autostart_set(wanted))
			{
				SendDlgItemMessageA(window, ID_AUTOSTART, BM_SETCHECK, wanted ? BST_UNCHECKED : BST_CHECKED, 0);
				set_status("Das ließ sich nicht umstellen.");
			}
			break;
		}
		case ID_SIGN_OUT:
			launcher_logout(ui.launcher);
			set_status("");
			show_page(false);
			break;
		}

		return 0;

	case WM_JOB_PROGRESS:
		SendDlgItemMessageA(window, ID_PROGRESS, PBM_SETPOS, wparam, 0);
		return 0;

	case WM_JOB_DONE:
		finish_job((Job*)lparam);
		return 0;

	case WM_DESTROY:
		PostQuitMessage(0);
		return 0;
	}

	return DefWindowProcA(window, message, wparam, lparam);
}

s32 ui_run(Launcher* launcher)
{
	ui.launcher = launcher;

	INITCOMMONCONTROLSEX controls = { sizeof(controls), ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES };
	InitCommonControlsEx(&controls);

	WNDCLASSA window_class;

	memset(&window_class, 0, sizeof(window_class));
	window_class.lpfnWndProc = window_proc;
	window_class.hInstance = GetModuleHandleA(NULL);
	window_class.hCursor = LoadCursorA(NULL, IDC_ARROW);
	window_class.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
	window_class.lpszClassName = WINDOW_CLASS;
	RegisterClassA(&window_class);

	// the size asked for is the inside of the window: the frame and the title bar come on top
	RECT frame = { 0, 0, WINDOW_WIDTH, WINDOW_HEIGHT };
	DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;

	AdjustWindowRect(&frame, style, FALSE);

	HWND window = CreateWindowExA(0, WINDOW_CLASS, "Playtest64 Launcher", style, CW_USEDEFAULT, CW_USEDEFAULT,
		frame.right - frame.left, frame.bottom - frame.top, NULL, NULL, window_class.hInstance, NULL);

	if (window == NULL)
		return 1;

	ShowWindow(window, SW_SHOWNORMAL);
	UpdateWindow(window);

	MSG message;

	while (GetMessageA(&message, NULL, 0, 0) > 0)
	{
		if (!IsDialogMessageA(window, &message))
		{
			TranslateMessage(&message);
			DispatchMessageA(&message);
		}
	}

	return (s32)message.wParam;
}
