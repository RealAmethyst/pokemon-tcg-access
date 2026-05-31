using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PokemonTCGLiveAccessibility.Navigation;
using PokemonTCGLiveAccessibility.Speech;
using PokemonTCGLiveAccessibility.Util;
using SharedLogicUtils.source.LearningLab;
using TPCI.Localization;
using UnityEngine;
using UnityEngine.UI;
using _Rainier.Features.LearningLab;

namespace PokemonTCGLiveAccessibility.Screens;

// The Learning Lab lesson list, modeled as a SECTION of the HOME screen (it lives under HOME's
// LEARN game-mode tab). HUBHomeScreen adds one of these as its main-content section whenever the
// list is showing, so Down from the game-mode tabs (or Enter on the active LEARN tab) drops into
// the lessons and Up returns to the tabs — exactly like every other HOME section boundary.
//
// It's a DRILL-DOWN: each level is a ClassroomData whose LearningLabData holds child classrooms
// (BASIC LESSONS, …) and/or lessons. Like the deck library we navigate the DATA (captured from
// SetupDisplay), not the virtualized scroller cells. The static SetupDisplay patch is the single
// source of truth for "the level changed" — open / drill-in / back all route through it — and
// HOME forwards that to the live section instance so a drill re-reads the new level.
internal sealed class LearningLabSection : INavSection
{
    // ---- static state, fed by the SetupDisplay patch -------------------------------------
    public static LearningLabClassroomListDisplay? Display { get; private set; }
    public static LearningLabScreenController? Controller { get; private set; }
    public static ClassroomData? Current { get; private set; }
    public static event Action? LevelChanged;

    // The list is "showing" only while its GameObject is live in the hierarchy (LEARN selected).
    public static bool IsShowing => Display != null && Display.gameObject.activeInHierarchy && Current != null;

    private static readonly MethodInfo? ClassroomBtnAction = AccessTools.Method(typeof(LearningLabScreenController), "ClassroomButtonAction");

    public static void Install(HarmonyLib.Harmony harmony)
    {
        var setup = AccessTools.Method(typeof(LearningLabClassroomListDisplay), "SetupDisplay");
        if (setup == null) { Log.Error("LearningLabSection: SetupDisplay not found"); return; }
        harmony.Patch(setup, postfix: new HarmonyMethod(AccessTools.Method(typeof(LearningLabSection), nameof(Setup_Postfix))));
        Log.Info("LearningLabSection installed");
    }

    private static void Setup_Postfix(LearningLabClassroomListDisplay __instance, ClassroomData classroomData)
    {
        Display = __instance;
        Controller = ReadField<LearningLabScreenController>(__instance, "learningLabScreenController");
        Current = classroomData;
        LevelChanged?.Invoke();
    }

    // ---- instance: the section nav -------------------------------------------------------
    private readonly SpeechEngine _speech;
    private readonly Action _escapeToParent;
    private int _index;
    private int _lineIndex = -1;
    private readonly List<string> _lines = new();

    public LearningLabSection(SpeechEngine speech, Action escapeToParent)
    {
        _speech = speech;
        _escapeToParent = escapeToParent;
        _index = 0;
    }

    private static readonly List<ILearningLabData> EmptyList = new();
    private static List<ILearningLabData> Items => Current?.LearningLabData ?? EmptyList;

    public int Index => _index;
    public int Count => Items.Count;
    public bool IsAtTopRow => _index <= 0;                 // single column → top row is item 0
    public bool IsAtBottomRow => _index >= Count - 1;      // …and bottom row is the last item
    public int CurrentColumn => 0;

    public void FocusFirst(bool announce = true, bool interruptPrior = true)
    {
        _index = 0; _lineIndex = -1;
        if (announce) AnnounceItem(interruptPrior, withLevelName: true);
    }

    // Entering this section from an adjacent section (Down-escape from the tabs, or diving in). Per-
    // section memory: keep our own last lesson position if it's still valid, so leaving and coming
    // back lands where the user was — only fall to the first lesson on a genuine first visit. (Level
    // changes reset _index to 0 via OnLevelChanged, so a stale index never survives a drill in/out.)
    public void FocusFirstRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        if (_index < 0 || _index >= Count) _index = 0;
        _lineIndex = -1;
        if (announce) AnnounceItem(interruptPrior, withLevelName: true);
    }

    public void FocusLastRow(int preferredColumn = -1, bool announce = true, bool interruptPrior = true)
    {
        // Per-section memory first; only fall to the last lesson on a first visit.
        if (_index < 0 || _index >= Count) _index = Mathf.Max(0, Count - 1);
        _lineIndex = -1;
        if (announce) AnnounceItem(interruptPrior, withLevelName: true);
    }

    public void RefocusCurrent(bool announce = true, bool interrupt = true)
    {
        if (_index < 0 || _index >= Count) _index = 0;
        if (announce) AnnounceItem(interrupt, withLevelName: false);
    }

    // Called by HUBHomeScreen when the SetupDisplay patch fires while this section is active
    // (a drill-in or a back-out changed the level) — reset to the new level and read it.
    public void OnLevelChanged()
    {
        _index = 0; _lineIndex = -1;
        AnnounceItem(interrupt: true, withLevelName: true);
    }

    public void Tick()
    {
        if (!Application.isFocused) return;
        // Up/Down at the section's edge are consumed by HUBHomeScreen for section escape; we
        // only see them mid-list, where they move between items.
        if (UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { Move(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { Move(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow)) { ReadLine(+1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow)) { ReadLine(-1); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter)) { Activate(); return; }
        if (UnityEngine.Input.GetKeyDown(KeyCode.Backspace) || UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Back(); return; }
    }

    private void Move(int dir)
    {
        int next = _index + dir;
        if (next < 0 || next >= Count) return;
        _index = next; _lineIndex = -1;
        AnnounceItem(interrupt: true, withLevelName: false);
    }

    private void Activate()
    {
        var items = Items;
        if (_index < 0 || _index >= items.Count || Controller == null) return;
        var item = items[_index];
        if (item is ClassroomData c)
        {
            // Drill in → the controller re-runs SetupDisplay → our patch → OnLevelChanged reads it.
            try { ClassroomBtnAction?.Invoke(Controller, new object[] { c }); }
            catch (Exception ex) { Log.Warn($"LL drill failed: {ex.Message}"); }
        }
        else if (item is LessonData l)
        {
            try { Controller.RunLesson(l.LessonControllerName, l.LessonKey, _index); }
            catch (Exception ex) { Log.Warn($"LL run lesson failed: {ex.Message}"); }
        }
    }

    private void Back()
    {
        // NavigationStepBack at the top level empties the nav stack (TryPop then a failing
        // TryPeek), so gate on the game's own backButton.interactable (true only when there's a
        // level to return to). At the top level, hand control back up to the game-mode tabs.
        var backBtn = Display != null ? ReadField<Button>(Display, "backButton") : null;
        bool canBack = false;
        try { canBack = backBtn != null && backBtn.interactable; } catch { }
        if (canBack) { try { Controller?.NavigationStepBack(); } catch (Exception ex) { Log.Warn($"LL back failed: {ex.Message}"); } }
        else _escapeToParent();
    }

    private void AnnounceItem(bool interrupt, bool withLevelName)
    {
        var items = Items;
        _lines.Clear(); _lineIndex = -1;
        if (_index < 0 || _index >= items.Count)
        {
            _speech.Say(withLevelName ? $"{LevelName()}, empty" : "empty", interrupt);
            return;
        }
        if (withLevelName)
        {
            _speech.Say(LevelName(), interrupt);
            _speech.Say(Announce.Item(ItemLabel(items[_index]), null, _index + 1, items.Count), interrupt: false);
        }
        else
        {
            _speech.Say(Announce.Item(ItemLabel(items[_index]), null, _index + 1, items.Count), interrupt);
        }
    }

    private void ReadLine(int dir)
    {
        var items = Items;
        if (_index < 0 || _index >= items.Count) return;
        if (_lines.Count == 0) BuildLines(items[_index]);
        int next = _lineIndex + dir;
        if (next < -1) return;
        if (next == -1) { _lineIndex = -1; _speech.Say(ItemLabel(items[_index]), interrupt: true); return; }
        if (next >= _lines.Count) return;
        _lineIndex = next;
        _speech.Say(_lines[_lineIndex], interrupt: true);
    }

    private void BuildLines(ILearningLabData item)
    {
        _lines.Clear();
        var desc = item is ClassroomData c ? Loc(c.DescriptionLocId) : item is LessonData l ? Loc(l.DescriptionLocId) : null;
        if (!string.IsNullOrWhiteSpace(desc)) _lines.Add(desc!);
    }

    private static string LevelName() => Current != null ? Loc(Current.NameLocId) : "Learning Lab";

    private static string ItemLabel(ILearningLabData item)
    {
        if (item is ClassroomData c)
        {
            var (done, total) = Progress(c);
            return $"{Loc(c.NameLocId)}, {c.Difficulty}, {done} of {total} lessons";
        }
        if (item is LessonData l)
            return $"{Loc(l.NameLocId)}, {(l.LessonStatus == LessonStatus.Complete ? "complete" : "not complete")}";
        return "item";
    }

    private static (int done, int total) Progress(ClassroomData c)
    {
        int done = 0, total = 0;
        void Walk(List<ILearningLabData> list)
        {
            foreach (var d in list)
            {
                if (d is LessonData ld) { total++; if (ld.LessonStatus == LessonStatus.Complete) done++; }
                else if (d is ClassroomData cd) Walk(cd.LearningLabData);
            }
        }
        Walk(c.LearningLabData);
        return (done, total);
    }

    private static string Loc(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        try { var v = LocalizationManager.Instance?.GetString(id); if (!string.IsNullOrWhiteSpace(v) && !v!.Contains("(Missing)")) return v; } catch { }
        return id!;
    }

    private static T? ReadField<T>(object instance, string name) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        for (var t = instance.GetType(); t != null; t = t.BaseType)
        { var f = t.GetField(name, flags); if (f != null) return f.GetValue(instance) as T; }
        return null;
    }
}
