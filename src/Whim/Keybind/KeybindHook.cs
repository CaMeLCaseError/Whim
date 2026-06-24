using System.Linq;
using System.Threading;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Whim;

/// <summary>
/// Responsible is responsible for hooking into windows and handling keybinds.
/// </summary>
internal class KeybindHook : IKeybindHook
{
	private readonly IContext _context;
	private readonly IInternalContext _internalContext;
	private readonly HOOKPROC _lowLevelKeyboardProc;
	private UnhookWindowsHookExSafeHandle? _unhookKeyboardHook;
	private bool _disposedValue;

	// Non-system modifiers (e.g. muhenkan) that Whim is currently swallowing, tracked from the
	// hook's own key down/up events. This is necessary because GetAsyncKeyState does not report a
	// key whose events the hook swallows, so we cannot use it to detect these modifiers.
	private readonly HashSet<VIRTUAL_KEY> _pressedSwallowedModifiers = [];

	// The hook runs on its own dedicated thread (see PostInitialize) rather than Whim's UI thread.
	private Thread? _hookThread;
	private uint _hookThreadId;
	private readonly ManualResetEventSlim _hookInstalled = new(false);

	public KeybindHook(IContext context, IInternalContext internalContext)
	{
		_context = context;
		_internalContext = internalContext;
		_lowLevelKeyboardProc = LowLevelKeyboardProcWrapper;
	}

	public void PostInitialize()
	{
		Logger.Debug("Initializing keybind manager...");

		// Run the low-level keyboard hook on its own dedicated, above-normal-priority thread with its
		// own message loop, rather than on Whim's UI thread. A WH_KEYBOARD_LL callback runs on the
		// thread that installed the hook; if that thread doesn't service the callback within Windows'
		// LowLevelHooksTimeout (~300ms), the keystroke is delivered to the focused window instead. On
		// the UI thread that happens whenever it's busy - e.g. laying out windows while rapidly
		// switching workspaces, or when another process saturates the CPU - causing keys to leak
		// through. A dedicated thread keeps key interception responsive regardless of UI-thread load.
		_hookThread = new Thread(HookThreadProc)
		{
			Name = "Whim keyboard hook",
			IsBackground = true,
			Priority = ThreadPriority.AboveNormal,
		};
		_hookThread.Start();

		// Block until the hook is installed, so callers can rely on it being active once this returns.
		if (!_hookInstalled.Wait(TimeSpan.FromSeconds(5)))
		{
			Logger.Error("Timed out waiting for the keyboard hook to be installed");
		}
	}

	private void HookThreadProc()
	{
		_hookThreadId = _internalContext.CoreNativeManager.GetCurrentThreadId();
		_unhookKeyboardHook = _internalContext.CoreNativeManager.SetWindowsHookEx(
			WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
			_lowLevelKeyboardProc,
			null,
			0
		);
		_hookInstalled.Set();

		// A low-level hook requires its owning thread to pump messages. Keep pumping until WM_QUIT is
		// posted by Dispose, then unhook.
		while (_internalContext.CoreNativeManager.GetMessage(out MSG msg, default, 0, 0).Value > 0)
		{
			PInvoke.TranslateMessage(msg);
			PInvoke.DispatchMessage(msg);
		}

		_unhookKeyboardHook?.Dispose();
	}

	private LRESULT LowLevelKeyboardProcWrapper(int nCode, WPARAM wParam, LPARAM lParam)
	{
		try
		{
			return LowLevelKeyboardProc(nCode, wParam, lParam);
		}
		catch (Exception e)
		{
			_context.HandleUncaughtException(nameof(LowLevelKeyboardProc), e);
			return _internalContext.CoreNativeManager.CallNextHookEx(nCode, wParam, lParam);
		}
	}

	/// <summary>
	/// For relevant documentation, see https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc
	/// </summary>
	/// <param name="nCode"></param>
	/// <param name="wParam"></param>
	/// <param name="lParam"></param>
	/// <returns></returns>
	private LRESULT LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam)
	{
		Logger.Verbose($"{nCode} {wParam.Value} {lParam.Value}");

		nuint message = (nuint)wParam;
		bool isKeyDown = message == PInvoke.WM_KEYDOWN || message == PInvoke.WM_SYSKEYDOWN;
		bool isKeyUp = message == PInvoke.WM_KEYUP || message == PInvoke.WM_SYSKEYUP;

		if (nCode != 0 || (!isKeyDown && !isKeyUp))
		{
			return _internalContext.CoreNativeManager.CallNextHookEx(nCode, wParam, lParam);
		}

		if (_internalContext.CoreNativeManager.PtrToStructure<KBDLLHOOKSTRUCT>(lParam) is not KBDLLHOOKSTRUCT kbdll)
		{
			return _internalContext.CoreNativeManager.CallNextHookEx(nCode, wParam, lParam);
		}

		VIRTUAL_KEY key = (VIRTUAL_KEY)kbdll.vkCode;

		// The pressed key is itself one of Whim's modifiers.
		if (_context.KeybindManager.Modifiers.Contains(key))
		{
			// System modifiers (Alt/Ctrl/Shift/Win) are passed through so applications can still
			// use them (Alt+Tab, Ctrl+C, etc.). Non-system keys repurposed as Whim modifiers - e.g.
			// the muhenkan/VK_OEM_PA1 key - are swallowed instead, so pressing them on their own
			// doesn't leak a character (such as "@") into the focused window.
			if (IsSystemModifier(key))
			{
				return _internalContext.CoreNativeManager.CallNextHookEx(nCode, wParam, lParam);
			}

			// Track the swallowed modifier's state ourselves (GetAsyncKeyState can't see it), so it
			// can still be matched when an action key is pressed.
			if (isKeyDown)
			{
				_pressedSwallowedModifiers.Add(key);
			}
			else
			{
				_pressedSwallowedModifiers.Remove(key);
			}

			return (LRESULT)1;
		}

		// Other keys only trigger keybinds on key-down.
		if (isKeyDown && GetKeybindForKey(key) is Keybind keybind && DoKeyboardEvent(keybind))
		{
			return (LRESULT)1;
		}

		return _internalContext.CoreNativeManager.CallNextHookEx(nCode, wParam, lParam);
	}

	private IKeybind? GetKeybindForKey(VIRTUAL_KEY eventKey)
	{
		List<VIRTUAL_KEY> pressedModifiers = [];
		foreach (VIRTUAL_KEY modifier in _context.KeybindManager.Modifiers)
		{
			if (IsModifierPressed(modifier))
			{
				pressedModifiers.Add(modifier);
			}
		}

		return new Keybind(pressedModifiers, eventKey);
	}

	// A modifier counts as pressed if either we are tracking it as a currently-held swallowed
	// modifier (GetAsyncKeyState can't see keys the hook swallows), or - for pass-through system
	// modifiers - GetAsyncKeyState reports it as physically down. GetAsyncKeyState is used rather
	// than GetKeyState (the calling thread's queued state), because the queued state can lag behind
	// near-simultaneous presses - e.g. pressing a modifier and an action key together - causing the
	// modifier to be missed and the action key to leak through to the focused window.
	private bool IsModifierPressed(VIRTUAL_KEY key) =>
		_pressedSwallowedModifiers.Contains(key)
		|| (_internalContext.CoreNativeManager.GetAsyncKeyState((int)key) & 0x8000) == 0x8000;

	/// <summary>
	/// The standard Windows modifier keys (Alt/Ctrl/Shift/Win, left and right variants). These are
	/// passed through to applications when pressed, unlike non-system keys repurposed as Whim
	/// modifiers, which are swallowed.
	/// </summary>
	private static readonly HashSet<VIRTUAL_KEY> _systemModifiers =
	[
		VIRTUAL_KEY.VK_LCONTROL,
		VIRTUAL_KEY.VK_RCONTROL,
		VIRTUAL_KEY.VK_CONTROL,
		VIRTUAL_KEY.VK_LSHIFT,
		VIRTUAL_KEY.VK_RSHIFT,
		VIRTUAL_KEY.VK_SHIFT,
		VIRTUAL_KEY.VK_LMENU,
		VIRTUAL_KEY.VK_RMENU,
		VIRTUAL_KEY.VK_MENU,
		VIRTUAL_KEY.VK_LWIN,
		VIRTUAL_KEY.VK_RWIN,
	];

	private static bool IsSystemModifier(VIRTUAL_KEY key) => _systemModifiers.Contains(key);

	private bool DoKeyboardEvent(Keybind keybind)
	{
		Logger.Verbose(keybind.ToString());
		ICommand[] commands = _context.KeybindManager.GetCommands(keybind);

		if (commands.Length == 0)
		{
			Logger.Verbose($"No handler for {keybind}");
			return false;
		}

		// Execute the commands asynchronously on the UI thread so the hook callback returns
		// immediately. Running a command (e.g. a workspace switch) synchronously here blocks the
		// hook; if it exceeds Windows' LowLevelHooksTimeout (~300ms), later key events bypass the
		// hook and leak to the focused window, and key-up events can be missed (leaving modifiers
		// stuck). Returning fast keeps key grabbing robust even while a switch or layout runs, or
		// when another process is starving the UI thread.
		_context.NativeManager.TryEnqueue(() =>
		{
			foreach (ICommand command in commands)
			{
				command.TryExecute();
			}
		});

		return true;
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposedValue)
		{
			if (disposing)
			{
				// Signal the hook thread's message loop to exit; the thread then unhooks the keyboard
				// hook and terminates. Wait for it so the hook is removed before we return.
				if (_hookThreadId != 0)
				{
					_internalContext.CoreNativeManager.PostThreadMessage(
						_hookThreadId,
						PInvoke.WM_QUIT,
						default,
						default
					);
				}

				_hookThread?.Join(TimeSpan.FromSeconds(5));

				// The hook thread unhooks on exit; dispose again here (idempotent) to cover the case
				// where the thread never started, and to make ownership explicit.
				_unhookKeyboardHook?.Dispose();
				_hookInstalled.Dispose();
			}

			// free unmanaged resources (unmanaged objects) and override finalizer
			// set large fields to null
			_disposedValue = true;
		}
	}

	public void Dispose()
	{
		// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}
