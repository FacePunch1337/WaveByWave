using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using WaveByWave.Enemies;

namespace WaveByWave.Editor
{
    public static partial class EnemySpeciesSetup
    {
        internal const string Folder = "Assets/_Project/Data/Enemies/Species";
        internal const string ProfileFolder = "Assets/_Project/Resources/Enemies";
        internal const string PrefabFolder = "Assets/_Project/Prefabs/Enemies/Species";
        internal const string SharkSource = "Assets/Alstra Infinite/Fish - PolyPack/Prefabs/SharkV1.prefab";
        internal const string TrollSource = "Assets/Troll_Сannibal/Prefabs/Troll_cannibal.prefab";
        private const string Request = "Temp/EnemySpecies.request";

        [InitializeOnLoadMethod]
        private static void Install() => EditorApplication.update += Requested;
        private static void Requested()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command;
            try
            {
                command = File.ReadAllText(Request).Trim();
                if (command.Length == 0) return;
                File.Delete(Request);
            }
            catch (IOException) { return; } // A request may still be being written by an external editor tool.
            try
            {
                RunSetup(command);
            }
            catch (Exception e) { File.WriteAllText("Temp/EnemySpecies.result", "FAIL " + e); Debug.LogException(e); }
        }
        private static void RunSetup(string command)
        {
            if (command == "bake") CreateAndBake();
            else if (command == "check") ValidateSpecies();
            else throw new InvalidOperationException(command);
        }
    }
}
