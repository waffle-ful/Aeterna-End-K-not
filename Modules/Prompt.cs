using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace EndKnot.Modules;

public static class Prompt
{
    private static readonly List<(string Question, Action OnYes, Action OnNo, float AutoDismiss)> Queue = [];

    private static SimpleButton YesButton;
    private static SimpleButton NoButton;
    private static string CurrentQuestion = string.Empty;
    private static bool ShowBackButton;

    // Incremented for every prompt actually put on screen, so a pending auto-dismiss timer can tell
    // "still my prompt" from "the user already answered and the next queued prompt took over".
    private static int Serial;

    /// <param name="autoDismissSeconds">If &gt; 0, the prompt closes itself through the "No" path after this many seconds (unattended hosts).</param>
    public static void Show(string question, Action onYes, Action onNo, bool showBackButton = false, float autoDismissSeconds = 0f)
    {
        try
        {
            if (!HudManager.InstanceExists) return;
            HudManager hud = HudManager.Instance;

            if (CurrentQuestion != string.Empty || !hud)
            {
                if (Queue.All(x => x.Question != question) && CurrentQuestion != question)
                    Queue.Add((question, onYes, onNo, autoDismissSeconds));

                return;
            }

            CurrentQuestion = question;
            ShowBackButton = showBackButton;
            int serial = ++Serial;
            hud.ShowPopUp(question);
            if (!ShowBackButton) hud.Dialogue.BackButton.gameObject.SetActive(false);

            Action closePromt = () =>
            {
                HidePromt();
                CurrentQuestion = string.Empty;

                if (!ShowBackButton) hud.Dialogue.BackButton.gameObject.SetActive(true);
                hud.Dialogue.Hide();

                if (Queue.Count > 0)
                {
                    (string q, Action y, Action n, float dismiss) = Queue[0];
                    Queue.RemoveAt(0);
                    LateTask.New(() => Show(q, y, n, autoDismissSeconds: dismiss), 0.01f, log: false);
                }
            };

            onYes += closePromt;
            onNo += closePromt;

            YesButton = new SimpleButton(
                hud.Dialogue.transform,
                "PromtYesButton",
                new(1f, -1.25f),
                new(0, 255, 165, 255),
                new(0, 255, 255, 255),
                onYes,
                Translator.GetString("Yes"));

            NoButton = new SimpleButton(
                hud.Dialogue.transform,
                "PromtNoButton",
                new(-1f, -1.25f),
                new(0, 165, 255, 255),
                new(0, 255, 255, 255),
                onNo,
                Translator.GetString("No"));

            if (autoDismissSeconds > 0f)
            {
                Action dismiss = onNo;

                LateTask.New(() =>
                {
                    // Answered already (or a queued prompt took the screen) -> the timer is stale.
                    if (Serial != serial || CurrentQuestion != question) return;

                    try { dismiss(); }
                    catch (Exception e) { Utils.ThrowException(e); }
                }, autoDismissSeconds, log: false);
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void HidePromt()
    {
        Object.Destroy(YesButton?.Button.gameObject);
        Object.Destroy(NoButton?.Button.gameObject);

        YesButton = null;
        NoButton = null;
    }

    private static void ClearQueue()
    {
        Queue.Clear();
    }

    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.Hide))]
    static class DialogueBoxHidePatch
    {
        public static bool Prefix()
        {
            if (CurrentQuestion != string.Empty)
            {
                if (!ShowBackButton) return false;

                ClearQueue();
                HidePromt();
                CurrentQuestion = string.Empty;
            }

            return true;
        }
    }
}