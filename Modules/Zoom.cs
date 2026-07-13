using System;
using System.Collections.Generic;
using UnityEngine;

namespace EndKnot;

// Credit: https://github.com/tugaru1975/TownOfPlus/TOPmods/Zoom.cs 
// Credit: https://github.com/Yumenopai/TownOfHost_Y
//[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
public static class Zoom
{
    private static bool ResetButtons;
    private static Camera Main;

    public static bool CanZoom => ((GameStates.IsShip && !GameStates.IsMeeting && GameStates.IsCanMove && !PlayerControl.LocalPlayer.IsAlive()) || (GameStates.IsLobby && GameStates.IsCanMove)) && !InGameRoleInfoMenu.Showing;

    public static void Postfix()
    {
        try
        {
            if (!Main) Main = Camera.main;
            if (!Main) return;

            if (CanZoom)
            {
                if (!ClientControlGUI.Instance.IsOpen)
                {
                    if (Main.orthographicSize > 3.0f) ResetButtons = true;

                    if (Input.touchSupported && !MapBehaviour.Instance.IsOpen)
                    {
                        if (Input.touchCount == 2)
                        {
                            Touch touch0 = Input.GetTouch(0);
                            Touch touch1 = Input.GetTouch(1);

                            Vector2 touch0PrevPos = touch0.position - touch0.deltaPosition;
                            Vector2 touch1PrevPos = touch1.position - touch1.deltaPosition;

                            float prevTouchDeltaMag = (touch0PrevPos - touch1PrevPos).magnitude;
                            float currentTouchDeltaMag = (touch0.position - touch1.position).magnitude;
                            float deltaMagnitudeDiff = currentTouchDeltaMag - prevTouchDeltaMag;

                            switch (deltaMagnitudeDiff)
                            {
                                case > 0:
                                {
                                    if (Main.orthographicSize > 3.0f)
                                        SetZoomSize();

                                    break;
                                }
                                case < 0:
                                {
                                    // lobby zoom-out は世界観破壊 (Backrooms grid 全露出) のため dev 限定。
                                    // 2026-05-22: `|| GameStates.IsLobby` を撤去し AmDebugger だけ通す
                                    if (GameStates.IsDead || GameStates.IsFreePlay || DebugModeManager.AmDebugger)
                                    {
                                        if (Main.orthographicSize < 18.0f)
                                            SetZoomSize(true);
                                    }

                                    break;
                                }
                            }
                        }
                    }

                    switch (Input.mouseScrollDelta.y)
                    {
                        case > 0:
                        {
                            if (Main.orthographicSize > 3.0f)
                                SetZoomSize();

                            break;
                        }
                        case < 0:
                        {
                            // lobby zoom-out は dev 限定 (Backrooms 世界観保護)
                            if (GameStates.IsDead || GameStates.IsFreePlay || DebugModeManager.AmDebugger)
                            {
                                if (Main.orthographicSize < 18.0f)
                                    SetZoomSize(true);
                            }

                            break;
                        }
                    }
                    Flag.NewFlag("Zoom");
                }
            }
            else
                Flag.Run(() => SetZoomSize(reset: true), "Zoom");
        }
        catch { }
    }

    public static void SetZoomSize(bool times = false, bool reset = false)
    {
        if (!Main || !HudManager.InstanceExists) return;

        var size = 1.5f;
        if (!times) size = 1 / size;

        if (reset)
        {
            Reset();
        }
        else
        {
            Main.orthographicSize *= size;
            HudManager.Instance.UICamera.orthographicSize *= size;

            if (Main.orthographicSize < 3.0f || HudManager.Instance.UICamera.orthographicSize < 3.0f)
                Reset();
        }

        HudManager.Instance.ShadowQuad?.gameObject.SetActive((reset || Mathf.Approximately(Main.orthographicSize, 3.0f)) && PlayerControl.LocalPlayer.IsAlive());

        if (ResetButtons)
        {
            ResolutionManager.ResolutionChanged.Invoke((float)Screen.width / Screen.height, Screen.width, Screen.height, Screen.fullScreen);
            ResetButtons = false;
        }

        return;

        static void Reset()
        {
            Main.orthographicSize = 3.0f;
            HudManager.Instance.UICamera.orthographicSize = 3.0f;
            HudManager.Instance.Chat.transform.localScale = Vector3.one;
            if (GameStates.IsMeeting) MeetingHud.Instance.transform.localScale = Vector3.one;
        }
    }

    public static void OnFixedUpdate()
    {
        if (!Main || !HudManager.InstanceExists) return;

        // ロビーでは IsAlive を見ない — 前ゲームの MainRole (GM/観戦者) が PlayerStates に残留したまま
        // IsAlive()=false になり、ゲーム後ロビーの影 (Backrooms 含む) が毎フレ強制 OFF される (BUG-20260706-03)。
        HudManager.Instance?.ShadowQuad?.gameObject.SetActive(Mathf.Approximately(Main.orthographicSize, 3.0f) && (GameStates.IsLobby || PlayerControl.LocalPlayer.IsAlive()));
    }
}

public static class Flag
{
    private static readonly List<string> OneTimeList = [];
    private static readonly List<string> FirstRunList = [];

    public static void Run(Action action, string type, bool firstrun = false)
    {
        if (OneTimeList.Contains(type) || (firstrun && !FirstRunList.Contains(type)))
        {
            if (!FirstRunList.Contains(type)) FirstRunList.Add(type);

            OneTimeList.Remove(type);
            action();
        }
    }

    public static void NewFlag(string type)
    {
        if (!OneTimeList.Contains(type)) OneTimeList.Add(type);
    }
}