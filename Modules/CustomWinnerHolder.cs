using System;
using System.Collections.Generic;
using Hazel;

namespace EndKnot;

public static class CustomWinnerHolder
{
    // The winning team will be stored.
    // Used to determine background color of results, etc.
    // Note: When changing this variable, if you do not change WinnerRoles and WinnerIds at the same time, unexpected winners may appear.
    public static CustomWinner WinnerTeam;

    // Stores the team of additional winning players.
    // Used to display results.
    public static HashSet<AdditionalWinners> AdditionalWinnerTeams;

    // The winning role is stored, and all players whose roles are stored in this variable win.
    // Ideal for handling team neutrals.
    public static HashSet<CustomRoles> WinnerRoles;

    // Stores the winner's PlayerID, all players with this ID win.
    // Ideal for handling neutrals that win alone.
    public static HashSet<byte> WinnerIds;

    // The win priority of the current WinnerTeam (from SoloWinOption). A higher value than this
    // takes the win away from the current winner; an equal value rides along as an additional winner.
    public static int WinPriority;

    public static void Reset()
    {
        WinnerTeam = CustomWinner.Default;
        AdditionalWinnerTeams = [];
        WinnerRoles = [];
        WinnerIds = [];
        WinPriority = -1;
        Logger.Info("Reset", "CustomWinnerHolder");
    }

    /// <summary>
    ///     <para>Assign a value to WinnerTeam. </para>
    ///     <para>Add to AdditionalWinnerTeams if already assigned.</para>
    /// </summary>
    public static void SetWinnerOrAdditonalWinner(CustomWinner winner)
    {
        if (WinnerTeam == CustomWinner.Default)
            WinnerTeam = winner;
        else
            AdditionalWinnerTeams.Add((AdditionalWinners)winner);

        Logger.Info($"WinnerTeam: {WinnerTeam}, AdditionalWinnerTeams: {string.Join(", ", AdditionalWinnerTeams)}", "CustomWinnerHolder.SetWinnerOrAdditonalWinner");
    }

    /// <summary>
    ///     <para>Assign a value to WinnerTeam. </para>
    ///     <para>If it is already assigned, add the existing value to AdditionalWinnerTeams and then assign it.</para>
    /// </summary>
    public static void ShiftWinnerAndSetWinner(CustomWinner winner)
    {
        if (WinnerTeam != CustomWinner.Default) AdditionalWinnerTeams.Add((AdditionalWinners)WinnerTeam);

        WinnerTeam = winner;
        Logger.Info($"WinnerTeam: {WinnerTeam}, AdditionalWinnerTeams: {string.Join(", ", AdditionalWinnerTeams)}", "CustomWinnerHolder.ShiftWinnerAndSetWinner");
    }

    /// <summary>
    ///     <para>Delete any existing values and then assign the values to WinnerTeam.</para>
    /// </summary>
    public static void ResetAndSetWinner(CustomWinner winner)
    {
        Reset();
        if (SoloWinOption.AllData.TryGetValue((CustomRoles)winner, out SoloWinOption data)) WinPriority = data.OptionWin.GetInt();
        WinnerTeam = winner;
        Logger.Info($"WinnerTeam: {WinnerTeam}", "CustomWinnerHolder.ResetAndSetWinner");
    }

    /// <summary>
    ///     <para>Resolves a winner against the current WinPriority (from SoloWinOption).</para>
    ///     <para>A higher priority takes over the win (resets everything first); an equal priority
    ///     with <paramref name="addWin"/> rides along as an additional winner; a lower priority is rejected.</para>
    /// </summary>
    /// <param name="winner">The candidate winner.</param>
    /// <param name="playerId">The winning player's id, or byte.MaxValue for a team-only win with no single player.</param>
    /// <param name="addWin">Whether an equal priority should ride along as an additional winner.</param>
    /// <param name="overrideRole">The role whose SoloWinOption to check, if it differs from <paramref name="winner"/>.</param>
    /// <returns>Whether the win was accepted (true for both takeover and ride-along).</returns>
    public static bool ResetAndSetAndChWinner(CustomWinner winner, byte playerId, bool addWin = true, CustomRoles overrideRole = CustomRoles.NotAssigned)
    {
        CustomRoles roleForPriority = overrideRole is CustomRoles.NotAssigned ? (CustomRoles)winner : overrideRole;

        if (!SoloWinOption.AllData.TryGetValue(roleForPriority, out SoloWinOption data))
        {
            Logger.Error($"{winner} has no SoloWinOption data", "CustomWinnerHolder.ResetAndSetAndChWinner");
            return false;
        }

        int priority = data.OptionWin.GetInt();

        if (WinPriority < priority)
        {
            Logger.Info($"{WinnerTeam} => {winner} (priority {WinPriority} < {priority})", "CustomWinnerHolder.ResetAndSetAndChWinner");
            Reset();
            WinPriority = priority;
            WinnerTeam = winner;
            if (playerId != byte.MaxValue) WinnerIds.Add(playerId);
            return true;
        }

        if (WinPriority == priority && addWin)
        {
            Logger.Info($"AddWin: {winner} (priority {priority})", "CustomWinnerHolder.ResetAndSetAndChWinner");
            if (Enum.IsDefined(typeof(AdditionalWinners), (int)winner))
                AdditionalWinnerTeams.Add((AdditionalWinners)winner);
            else
                WinnerRoles.Add((CustomRoles)winner);
            if (playerId != byte.MaxValue) WinnerIds.Add(playerId);
            return true;
        }

        Logger.Info($"{winner} rejected (priority {priority} <= {WinPriority})", "CustomWinnerHolder.ResetAndSetAndChWinner");
        return false;
    }

    public static MessageWriter WriteTo(MessageWriter writer)
    {
        writer.WritePacked((int)WinnerTeam);

        writer.WritePacked(AdditionalWinnerTeams.Count);
        foreach (AdditionalWinners wt in AdditionalWinnerTeams) writer.WritePacked((int)wt);

        writer.WritePacked(WinnerRoles.Count);
        foreach (CustomRoles wr in WinnerRoles) writer.WritePacked((int)wr);

        writer.WritePacked(WinnerIds.Count);
        foreach (byte id in WinnerIds) writer.Write(id);

        return writer;
    }

    public static void ReadFrom(MessageReader reader)
    {
        WinnerTeam = (CustomWinner)reader.ReadPackedInt32();

        AdditionalWinnerTeams = [];
        int AdditionalWinnerTeamsCount = reader.ReadPackedInt32();
        for (var i = 0; i < AdditionalWinnerTeamsCount; i++) AdditionalWinnerTeams.Add((AdditionalWinners)reader.ReadPackedInt32());

        WinnerRoles = [];
        int WinnerRolesCount = reader.ReadPackedInt32();
        for (var i = 0; i < WinnerRolesCount; i++) WinnerRoles.Add((CustomRoles)reader.ReadPackedInt32());

        WinnerIds = [];
        int WinnerIdsCount = reader.ReadPackedInt32();
        for (var i = 0; i < WinnerIdsCount; i++) WinnerIds.Add(reader.ReadByte());
    }
}