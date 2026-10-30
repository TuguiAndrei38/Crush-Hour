using UnityEngine;
using UnityEngine.SceneManagement;

namespace CrushHour
{
    /// <summary>
    /// Start menu (<see cref="Phase.Menu"/>): pick a map (a scene) and a difficulty, then Play. Picking another map's
    /// scene loads it (it must be in Build Settings) and that scene's game skips the menu once and goes straight to
    /// Planning. The crowd keeps milling about behind the menu.
    /// </summary>
    public partial class CrushHourGame
    {
        /// <summary>Maps in the menu: title, scene name (must be in File > Build Profiles / Build Settings), description.</summary>
        static readonly (string title, string scene, string description)[] Maps =
        {
            ("Summer Music Festival", "CrushHourFestival_Dev",
                "Open-air festival: main stage, food court, bar, dance tent and two light towers. 2 narrow entrances, 6 emergency gates."),
        };

        /// <summary>Set before loading a map's scene from the menu, so the new scene starts in Planning instead of the menu.</summary>
        static bool skipMenuOnce;
        /// <summary>Map highlighted in the menu (kept across scene loads).</summary>
        static int menuMap;
        string menuError;
        GUIStyle mapRowStyle, mapRowSelectedStyle;

        void OpenMenu()
        {
            phase = Phase.Menu;
            paused = false;
            selectedUnit = -1;
            menuError = null;
            CancelDrag();
            // Highlight this scene's map if it's in the list.
            string active = SceneManager.GetActiveScene().name;
            for (int i = 0; i < Maps.Length; i++)
                if (Maps[i].scene == active) menuMap = i;
        }

        /// <summary>Play: same scene, a new game at the chosen difficulty; another scene, load it.</summary>
        void PlayFromMenu()
        {
            PlayerPrefs.SetInt(DifficultyPref, (int)difficulty);
            PlayerPrefs.Save();
            var map = Maps[Mathf.Clamp(menuMap, 0, Maps.Length - 1)];
            if (map.scene != SceneManager.GetActiveScene().name)
            {
                if (!Application.CanStreamedLevelBeLoaded(map.scene))
                {
                    menuError = $"Scene \"{map.scene}\" isn't in Build Settings. Add it under File > Build Profiles (Scene List).";
                    return;
                }
                skipMenuOnce = true;
                SceneManager.LoadScene(map.scene);
                return;
            }
            NewGame(); // Planning, with the chosen difficulty's crowd
        }

        void DrawMenu()
        {
            if (mapRowStyle == null)
            {
                mapRowStyle = ButtonStyle(CardColor, 14, TextAnchor.MiddleLeft);
                mapRowSelectedStyle = ButtonStyle(SelectedColor, 14, TextAnchor.MiddleLeft);
                mapRowStyle.fontStyle = mapRowSelectedStyle.fontStyle = FontStyle.Normal;
                mapRowStyle.wordWrap = mapRowSelectedStyle.wordWrap = true;
            }

            var rect = Panel(new Rect(vw * 0.5f - 300, vh * 0.5f - 250, 600, 500), BoxStyle(new Color(0.09f, 0.1f, 0.14f, 0.97f), 12, 0));
            GUILayout.BeginArea(new Rect(rect.x + 28, rect.y + 22, rect.width - 56, rect.height - 44));
            GUILayout.Label("CRUSH HOUR", TextStyle(30, TextColor, FontStyle.Bold));
            GUILayout.Label("Keep the crowd alive when things go wrong.", mutedStyle);
            GUILayout.Space(14);

            GUILayout.Label("SELECT MAP", headerStyle);
            string active = SceneManager.GetActiveScene().name;
            for (int i = 0; i < Maps.Length; i++)
            {
                var map = Maps[i];
                string here = map.scene == active ? "  (this scene)" : "";
                string label = $"<b>{map.title}</b>{here}\n<size=11>{map.description}</size>";
                if (GUILayout.Button(label, i == menuMap ? mapRowSelectedStyle : mapRowStyle, GUILayout.Height(58)))
                {
                    menuMap = i;
                    menuError = null;
                }
            }
            GUILayout.Space(12);

            GUILayout.Label("SELECT DIFFICULTY", headerStyle);
            GUILayout.BeginHorizontal();
            foreach (Difficulty d in new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard })
            {
                var s = DifficultySettings.For(d);
                string label = $"{s.Name}\n<size=10>score ×{s.scoreMultiplier:0.#}</size>";
                if (GUILayout.Button(label, d == difficulty ? selectedButtonStyle : buttonStyle, GUILayout.Height(48))) difficulty = d;
            }
            GUILayout.EndHorizontal();
            var chosen = DifficultySettings.For(difficulty);
            int people = crowdSizeOverride > 0 ? Mathf.Max(100, crowdSizeOverride) : chosen.playerCount;
            GUILayout.Label($"{people} people{(crowdSizeOverride > 0 ? " (dev override)" : "")} · {chosen.drunkFraction * 100f:0}% drunk · " +
                            $"up to {chosen.maxBlockedDoors} blocked exit{(chosen.maxBlockedDoors > 1 ? "s" : "")} · " +
                            $"{chosen.disasterCount} disaster{(chosen.disasterCount > 1 ? "s" : "")} (fire, falling light tower, gas leak)", mutedStyle);

            GUILayout.FlexibleSpace();
            if (menuError != null) GUILayout.Label(menuError, TextStyle(12, WarningColor, FontStyle.Bold, TextAnchor.UpperLeft, true));
            if (GUILayout.Button("PLAY", goodButtonStyle, GUILayout.Height(48))) deferred.Add(PlayFromMenu);
            GUILayout.EndArea();
        }
    }
}
