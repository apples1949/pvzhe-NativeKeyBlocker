using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「屏蔽原生热键」Mod —— 把游戏自带的 6 个战斗快捷键各自做成独立配置开关。
///
/// ── 屏蔽对象（project.godot [input] 实读）────────────────────────
///   Shovel=1（铲子）/ PacketUIFront=2（切卡栏前置）/ ShowPlantHealth=3（植物血量）
///   ShowZombieHealth=4（僵尸血量）/ SpeedUp=5（加速）/ Glove=0（手套）
///
/// ── 实现原理 ─────────────────────────────────────────────────────
/// 这 6 个热键在游戏源码里全部是硬编码 Input.IsActionJustPressed（无游戏内改键入口），
/// 而 Godot InputMap 是运行时可改的：
///   · 屏蔽 = InputMap.ActionEraseEvents(action)：只删按键绑定、保留动作本身
///     ⇒ 游戏代码照常调用 IsActionJustPressed 但永远 false，不会抛错。
///   · 恢复 = 把启动时快照的 InputEvent 数组用 ActionAddEvent 原样还回去。
///   · 卸载本 Mod 时必须全部还原（Shutdown 兜底），不留脏状态。
/// 游戏自身不会在运行时重建这 6 个动作（全工程仅 ModEditor 的 mod/ 前缀动作
/// 用过 InputMap，见 XWModPresentationService.cs，与原生动作无交集），
/// 因此「状态对齐制」即可：期望集合没变就不动 InputMap，不每帧折腾。
///
/// ── 铁律 ──────────────────────────────────────────────────────────
/// · 手写 csproj ⇒ 无源生成器 ⇒ 不用 _Input/_Process；走 process_frame 信号。
/// · 入口三回调一律 try/catch 吞异常（抛出 = 整包回滚）。
/// </summary>
public sealed class NativeKeyBlockerEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string P = "[NativeKeyBlocker] ";

	private const string KEnabled = "enabled";
	private const string KLog = "enableLog";

	/// <summary>配置键 → InputMap 动作名 → （给人看的）中文名。</summary>
	private static readonly KeyValuePair<string, string>[] BlockMap =
	{
		new KeyValuePair<string, string>("blockShovel", "Shovel"),
		new KeyValuePair<string, string>("blockPacketUIFront", "PacketUIFront"),
		new KeyValuePair<string, string>("blockShowPlantHealth", "ShowPlantHealth"),
		new KeyValuePair<string, string>("blockShowZombieHealth", "ShowZombieHealth"),
		new KeyValuePair<string, string>("blockSpeedUp", "SpeedUp"),
		new KeyValuePair<string, string>("blockGlove", "Glove"),
	};

	private static readonly Dictionary<string, string> ActionLabel = new Dictionary<string, string>
	{
		{ "Shovel", "铲子(1)" },
		{ "PacketUIFront", "切卡栏前置(2)" },
		{ "ShowPlantHealth", "植物血量(3)" },
		{ "ShowZombieHealth", "僵尸血量(4)" },
		{ "SpeedUp", "加速(5)" },
		{ "Glove", "手套(0)" },
	};

	private XWModSettings _settings;
	private bool _subscribed;
	private SceneTree _tree;
	private Callable _tick;
	private bool _started;

	/// <summary>已生效的屏蔽集合（与期望集合比较，避免每帧动 InputMap）。</summary>
	private HashSet<string> _applied = new HashSet<string>();
	/// <summary>被屏蔽动作的原始事件快照（恢复用）。</summary>
	private readonly Dictionary<string, Godot.Collections.Array<InputEvent>> _savedEvents =
		new Dictionary<string, Godot.Collections.Array<InputEvent>>();
	private bool _initReported;

	public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
		try
		{
			try { _settings = context.Settings; }
			catch (Exception ex) { try { GD.PrintErr(P + "配置不可用：" + ex.GetBaseException().Message); } catch { } }
			try { if (false)   /* ★ 日志默认关闭（原：ReadBool(KLog)） */ { GD.Print(P + "初始化完成（6 个原生热键独立开关，默认全屏蔽）。"); } } catch { }
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null) { Warn("拿不到 SceneTree，本 Mod 不生效。"); return; }
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			Subscribe();
			_started = true;
			Log("已挂载 process_frame。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			Unsubscribe();
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			RestoreAll();   // ★ 卸载必须把原生键原样还回去
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally { _started = false; }
	}

	// ============================================================ 每帧（状态对齐）

	private void OnFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "屏蔽原生热键", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }

			var want = new HashSet<string>();
			if (ReadBool(KEnabled, true))   // ★ 还原：这是屏蔽热键的功能开关，不是日志开关
			{
				foreach (KeyValuePair<string, string> kv in BlockMap)
				{
					if (ReadBool(kv.Key, true)) { want.Add(kv.Value); }
				}
			}

			if (_initReported && SameSet(_applied, want)) { return; }

			// 先全部还原，再按期望重新屏蔽（快照始终基于干净状态）
			RestoreAll();
			foreach (string action in want)
			{
				try
				{
					if (!InputMap.HasAction(action)) { continue; }
					Godot.Collections.Array<InputEvent> evts = InputMap.ActionGetEvents(action);
					if (evts != null && evts.Count > 0)
					{
						Godot.Collections.Array<InputEvent> copy = new Godot.Collections.Array<InputEvent>();
						foreach (InputEvent ev in evts) { copy.Add(ev); }
						_savedEvents[action] = copy;
						InputMap.ActionEraseEvents(action);
					}
				}
				catch (Exception ex) { WarnOnce("block_" + action, "屏蔽 " + action + " 失败：" + ex.Message); }
			}
			_applied = new HashSet<string>(want);

			if (!_initReported)
			{
				_initReported = true;
				if (_applied.Count > 0)
				{
					Log("已屏蔽原生热键：" + JoinLabels(_applied) + "（各自开关见配置页）");
				}
				else
				{
					Log("所有屏蔽开关均为关，原生热键保持原样。");
				}
			}
			else
			{
				Log("屏蔽状态更新：当前屏蔽 [" + JoinLabels(_applied) + "]");
			}
		}
		catch (Exception ex)
		{
			WarnOnce("frame", "每帧异常：" + ex.GetBaseException().Message);
		}
	}

	private void RestoreAll()
	{
		try
		{
			foreach (KeyValuePair<string, Godot.Collections.Array<InputEvent>> kv in _savedEvents)
			{
				try
				{
					if (!InputMap.HasAction(kv.Key)) { continue; }
					InputMap.ActionEraseEvents(kv.Key);
					foreach (InputEvent ev in kv.Value)
					{
						if (ev != null) { InputMap.ActionAddEvent(kv.Key, ev); }
					}
				}
				catch { }
			}
		}
		catch { }
		_savedEvents.Clear();
		_applied = new HashSet<string>();
	}

	private static bool SameSet(HashSet<string> a, HashSet<string> b)
	{
		if (a.Count != b.Count) { return false; }
		foreach (string s in a) { if (!b.Contains(s)) { return false; } }
		return true;
	}

	private static string JoinLabels(HashSet<string> actions)
	{
		var parts = new List<string>();
		foreach (string a in actions)
		{
			parts.Add(ActionLabel.TryGetValue(a, out string lb) ? lb : a);
		}
		parts.Sort(StringComparer.Ordinal);
		return string.Join("、", parts);
	}

	// ============================================================ 配置

	private void Subscribe()
	{
		try
		{
			if (_settings == null || _subscribed) { return; }
			_settings.Changed += OnSettingsChanged;
			_subscribed = true;
		}
		catch (Exception ex) { WarnOnce("sub", "订阅配置失败：" + ex.GetBaseException().Message); }
	}

	private void Unsubscribe()
	{
		try
		{
			if (_settings != null && _subscribed)
			{
				_settings.Changed -= OnSettingsChanged;
				_subscribed = false;
			}
		}
		catch { }
	}

	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try { Log("配置已更新：" + string.Join("、", keys)); } catch { }
	}

	private bool ReadBool(string key, bool fallback)
	{
		if (_settings == null) { return fallback; }
		try
		{
			object v = _settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return fallback;
	}

	// ============================================================ 日志

	private readonly HashSet<string> _warnedOnce = new HashSet<string>();

	private void WarnOnce(string tag, string msg)
	{
		// ★ 2026-10-14：原先绕过日志开关，现统一受 enableLog 控制
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */
		{
			return;
		}
		if (_warnedOnce.Add(tag)) { try { GD.PrintErr(P + msg); } catch { } }
	}

	private void Log(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		try { GD.Print(P + msg); } catch { }
	}

	private void Warn(string msg)
	{
		if (true)    /* ★ 用户要求：日志默认关闭（原：!ReadBool(KLog, false)） */ { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try { GD.PrintErr(P + msg); } catch { }
	}
}
