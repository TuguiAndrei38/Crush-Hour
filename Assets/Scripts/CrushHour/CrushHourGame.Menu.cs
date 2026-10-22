using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>
    /// Main menu, pause menu and "how to play" screen (IMGUI, drawn over the generated festival).
    /// While a menu is open the simulation and all game input are frozen.
    /// Every state change goes through <c>deferred</c>, so the IMGUI layout never changes mid-OnGUI.
    /// </summary>
    public partial class CrushHourGame
    {
        enum MenuScreen { None, Main, HowToPlay, Pause }

        MenuScreen menu = MenuScreen.None;
        MenuScreen howToReturn = MenuScreen.Main;
        GUIStyle menuTitleStyle, menuTaglineStyle, menuBodyStyle, menuButtonStyle;

        bool MenuOpen => menu != MenuScreen.None;

        void OpenMenu(MenuScreen screen)
        {
            menu = screen;
            CancelDrag();
        }

        void HandleMenuKeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;
            switch (menu)
            {
                case MenuScreen.None: OpenMenu(MenuScreen.Pause); break;
                case MenuScreen.Pause: menu = MenuScreen.None; break;
                case MenuScreen.HowToPlay: menu = howToReturn; break;
            }
        }

        void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void GoToMainMenu()
        {
            NewGame();
            OpenMenu(MenuScreen.Main);
        }

        void OpenHowToPlay()
        {
            howToReturn = menu;
            menu = MenuScreen.HowToPlay;
        }

        // ================================================================ UI

        void EnsureMenuStyles()
        {
            if (menuTitleStyle != null) return;
            menuTitleStyle = new GUIStyle(titleStyle) { fontSize = 54, alignment = TextAnchor.MiddleCenter };
            menuTitleStyle.normal.textColor = new Color(1f, 0.55f, 0.15f);
            menuTaglineStyle = new GUIStyle(labelStyle) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            menuTaglineStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            menuBodyStyle = new GUIStyle(labelStyle) { fontSize = 14, wordWrap = true, richText = true };
            menuButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        }

        void DrawMenu()
        {
            EnsureMenuStyles();

            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, menu == MenuScreen.Main ? 0.6f : 0.7f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            switch (menu)
            {
                case MenuScreen.Main: DrawMainMenu(); break;
                case MenuScreen.Pause: DrawPauseMenu(); break;
                case MenuScreen.HowToPlay: DrawHowToPlay(); break;
            }
        }

        static Rect CenteredRect(float width, float height) =>
            new((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        bool MenuButton(string text, float height = 40f)
        {
            return GUILayout.Button(text, menuButtonStyle, GUILayout.Height(height));
        }

        void DrawMainMenu()
        {
            GUILayout.BeginArea(CenteredRect(420f, 480f));
            GUILayout.Label("CRUSH HOUR", menuTitleStyle, GUILayout.Height(70f));
            GUILayout.Label("You are the safety commander. When disaster hits, get everyone out alive.", menuTaglineStyle);
            GUILayout.Space(28f);

            if (MenuButton("PLAY   -   Summer Music Festival", 52f)) deferred.Add(() => menu = MenuScreen.None);
            GUILayout.Space(6f);

            GUI.enabled = false;
            MenuButton("Stadium Derby   (coming soon)");
            MenuButton("Metro Rush Hour   (coming soon)");
            GUI.enabled = true;
            GUILayout.Space(14f);

            if (MenuButton("How to play")) deferred.Add(OpenHowToPlay);
            if (MenuButton("Quit")) deferred.Add(QuitGame);
            GUILayout.EndArea();
        }

        void DrawPauseMenu()
        {
            GUILayout.BeginArea(CenteredRect(360f, 330f));
            GUILayout.Label("PAUSED", menuTitleStyle, GUILayout.Height(70f));
            GUILayout.Space(14f);

            if (MenuButton("Resume   [Esc]", 48f)) deferred.Add(() => menu = MenuScreen.None);
            GUILayout.Space(6f);
            if (MenuButton("Restart round   (new crowd, new incident)")) deferred.Add(() =>
            {
                NewGame();
                menu = MenuScreen.None;
            });
            if (MenuButton("How to play")) deferred.Add(OpenHowToPlay);
            if (MenuButton("Main menu")) deferred.Add(GoToMainMenu);
            if (MenuButton("Quit")) deferred.Add(QuitGame);
            GUILayout.EndArea();
        }

        void DrawHowToPlay()
        {
            GUILayout.BeginArea(CenteredRect(640f, 560f));
            GUILayout.Label("HOW TO PLAY", menuTitleStyle, GUILayout.Height(70f));
            GUILayout.Space(8f);

            GUILayout.Label(
                "<b>Goal.</b>  Thousands of fans are at the festival. A fire will break out at a random spot after the show starts. " +
                "Get as many people out safely as you can, with nobody hurt, as fast as you can.\n\n" +
                "<b>1. Planning.</b>  Draw barriers to split the crowd and move your stewards into position. Then press <b>Enter</b> to start the show.\n\n" +
                "<b>2. Evacuation.</b>  Open the red emergency gates, send stewards to calm and direct people, and use the PA. " +
                "People only know the entrances they came in by, and panicking people stop listening.\n\n" +
                "<b>Watch the pressure map.</b>  Orange means dense, red and purple mean people are being crushed. Spread the crowd over more exits.\n\n" +
                "<b>Controls</b>\n" +
                "   <b>1 / 2</b>  Command tool / Barrier tool        <b>F</b>  PA: evacuate        <b>C</b>  PA: stay calm\n" +
                "   <b>Space</b>  Pause        <b>Tab</b>  Speed        <b>H</b>  Pressure map        <b>R</b>  Restart\n" +
                "   <b>WASD</b> pan,  <b>Scroll</b> zoom,  <b>Q / E</b> rotate,  <b>Middle-drag</b> pan\n" +
                "   <b>Esc</b>  Menu",
                menuBodyStyle);

            GUILayout.FlexibleSpace();
            if (MenuButton("Back", 44f)) deferred.Add(() => menu = howToReturn);
            GUILayout.EndArea();
        }
    }
}
