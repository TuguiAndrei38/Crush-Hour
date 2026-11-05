using UnityEngine;

namespace CrushHour
{
    /// <summary>Operations Desk: paper venue dossiers, difficulty records and saved view options.</summary>
    public partial class CrushHourGame
    {
        const string MapPref = "CrushHour.Map";
        const string OptionsPref = "CrushHour.Options.";
        const float MenuWidth = 1600f, MenuHeight = 900f;
        enum MenuPage { Home, Maps, Difficulty, Options }
        MenuPage menuPage;
        VenueMap selectedMap;
        bool menuLaunchedGame, menuOptionsDirty;
        float cameraSensitivity = 1f;
        string MapTitle => MapCatalog.Get(selectedMap).Title;

        static readonly Color MenuInk = new(0.065f, 0.105f, 0.18f);
        static readonly Color MenuRed = new(0.73f, 0.105f, 0.085f);
        static readonly Color MenuPaper = new(0.94f, 0.906f, 0.83f);
        static readonly Color MenuHoverRed = new(0.66f, 0.15f, 0.13f);
        static readonly Color MenuHoverPaper = new(0.89f, 0.855f, 0.79f);
        static readonly Color MenuMuted = new(0.32f, 0.35f, 0.39f);
        static readonly Color MenuRule = new(0.25f, 0.29f, 0.34f, 0.6f);
        static readonly Color MenuYellow = new(1f, 0.76f, 0.16f);

        GUIStyle menuHeroStyle, menuHeroAccentStyle, menuTitleStyle, menuCardTitleStyle;
        GUIStyle menuBodyStyle, menuSmallStyle, menuEyebrowStyle, menuNumberStyle;
        GUIStyle menuButtonLabelStyle, menuWhiteButtonStyle, menuHomeActionStyle, menuHomeWhiteStyle;
        GUIStyle menuIndexStyle, menuWhiteIndexStyle, menuCenteredStyle, menuSliderStyle, menuThumbStyle;
        Texture2D menuPaperArt, menuStar, menuSlant;
        Texture2D[] menuPreviews;

        void LoadMenuPreferences()
        {
            selectedMap = MapCatalog.Normalize((VenueMap)PlayerPrefs.GetInt(MapPref, 0));
            showRegions = PlayerPrefs.GetInt(OptionsPref + "ZoneBoundaries", showRegions ? 1 : 0) != 0;
            heatmap = PlayerPrefs.GetInt(OptionsPref + "PressureMap", heatmap ? 1 : 0) != 0;
            stateMarkers = PlayerPrefs.GetInt(OptionsPref + "StateMarkers", stateMarkers ? 1 : 0) != 0;
            cameraSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(OptionsPref + "CameraSensitivity", 1f), 0.5f, 2f);
            cameraRig.inputSensitivity = cameraSensitivity;
        }

        void SaveMenuPreferences()
        {
            PlayerPrefs.SetInt(OptionsPref + "ZoneBoundaries", showRegions ? 1 : 0);
            PlayerPrefs.SetInt(OptionsPref + "PressureMap", heatmap ? 1 : 0);
            PlayerPrefs.SetInt(OptionsPref + "StateMarkers", stateMarkers ? 1 : 0);
            PlayerPrefs.SetFloat(OptionsPref + "CameraSensitivity", cameraSensitivity);
            PlayerPrefs.Save();
            menuOptionsDirty = false;
        }

        void OpenMenu()
        {
            phase = Phase.Menu;
            menuPage = MenuPage.Home;
            paused = false;
            selectedUnit = -1;
            cameraRig.controlsEnabled = false;
            CancelDrag();
        }

        void MenuBack()
        {
            if (menuOptionsDirty) SaveMenuPreferences();
            menuPage = menuPage == MenuPage.Difficulty ? MenuPage.Maps : MenuPage.Home;
        }

        void SelectNewLevelFromResults()
        {
            // Clear the finished incident before the menu's background simulation resumes.
            NewGame();
            OpenMenu();
            menuPage = MenuPage.Maps;
        }

        void PlayFromMenu()
        {
            selectedMap = MapCatalog.Normalize(selectedMap);
            PlayerPrefs.SetInt(MapPref, (int)selectedMap);
            PlayerPrefs.SetInt(DifficultyPref, (int)difficulty);
            PlayerPrefs.Save();
            speedIndex = 0;
            welcomeDismissed = false;
            // Every map is played from the control room. Load that scene unless we're already in it; it reads the
            // map and difficulty saved above and opens straight in Planning.
            if (!InControlRoom && Application.CanStreamedLevelBeLoaded(ControlRoomScene))
            {
                skipMenuOnce = launchedFromMenu = true;
                UnityEngine.SceneManagement.SceneManager.LoadScene(ControlRoomScene);
                return;
            }
            if (!InControlRoom) Debug.LogWarning($"Crush Hour: scene {ControlRoomScene} isn't in Build Settings (Tools > Crush Hour > Create Control Room Scene); playing top-down here.");
            menuLaunchedGame = true;
            NewGame();
        }

        void QuitFromMenu()
        {
            if (menuOptionsDirty) SaveMenuPreferences();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        GUIStyle OperationsText(Font font, int size, Color color, bool display = false,
            TextAnchor alignment = TextAnchor.UpperLeft, bool wrap = false)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = size,
                fontStyle = font != null ? FontStyle.Normal : display ? FontStyle.BoldAndItalic : FontStyle.Normal,
                alignment = alignment,
                wordWrap = wrap,
                richText = false,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            // Cloned label styles inherit the skin's hover colours. Keep the same readable
            // foreground in every state; the button background provides hover feedback.
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                         style.onNormal, style.onHover, style.onActive, style.onFocused })
                state.textColor = color;
            return style;
        }

        void EnsureMenuStyles()
        {
            if (menuHeroStyle != null) return;
            menuPaperArt = Resources.Load<Texture2D>("Menu/OperationsDeskPaper");
            Font display = Resources.Load<Font>("Menu/OperationsDeskDisplay");
            Font body = Resources.Load<Font>("Menu/OperationsDeskBody");
            menuHeroStyle = OperationsText(display, 194, MenuInk, true);
            menuHeroAccentStyle = OperationsText(display, 194, MenuRed, true);
            menuTitleStyle = OperationsText(display, 66, MenuInk, true);
            menuCardTitleStyle = OperationsText(display, 37, MenuInk, true);
            menuBodyStyle = OperationsText(body, 22, MenuInk, wrap: true);
            menuSmallStyle = OperationsText(body, 18, MenuMuted, wrap: true);
            menuEyebrowStyle = OperationsText(display, 26, MenuInk, true);
            menuNumberStyle = OperationsText(display, 86, MenuInk, true);
            menuButtonLabelStyle = OperationsText(display, 29, MenuInk, true, TextAnchor.MiddleCenter);
            menuWhiteButtonStyle = OperationsText(display, 29, MenuPaper, true, TextAnchor.MiddleCenter);
            menuHomeActionStyle = OperationsText(display, 72, MenuInk, true, TextAnchor.MiddleLeft);
            menuHomeWhiteStyle = OperationsText(display, 72, MenuPaper, true, TextAnchor.MiddleLeft);
            menuIndexStyle = OperationsText(display, 38, MenuInk, true, TextAnchor.MiddleCenter);
            menuWhiteIndexStyle = OperationsText(display, 38, MenuPaper, true, TextAnchor.MiddleCenter);
            menuCenteredStyle = OperationsText(body, 17, MenuMuted, alignment: TextAnchor.MiddleCenter);
            menuSliderStyle = new GUIStyle(GUI.skin.horizontalSlider) { fixedHeight = 8f, margin = new RectOffset(), padding = new RectOffset() };
            menuSliderStyle.normal.background = Texture2D.whiteTexture;
            menuThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb) { fixedWidth = 24f, fixedHeight = 30f, margin = new RectOffset(-2, -2, -11, 0), padding = new RectOffset() };
            menuThumbStyle.normal.background = menuThumbStyle.hover.background = menuThumbStyle.active.background = Texture2D.whiteTexture;
            menuSlant = CreateMenuSlant();
        }

        void DrawMenu()
        {
            EnsureMenuStyles();
            MenuFill(new Rect(0, 0, vw, vh), MenuPaper);
            Matrix4x4 previousMatrix = GUI.matrix;
            float fit = Mathf.Min(vw / MenuWidth, vh / MenuHeight);
            GUI.matrix = previousMatrix * Matrix4x4.TRS(
                new Vector3((vw - MenuWidth * fit) * 0.5f, (vh - MenuHeight * fit) * 0.5f, 0f),
                Quaternion.identity, new Vector3(fit, fit, 1f));
            try
            {
                if (menuPaperArt != null)
                    GUI.DrawTexture(new Rect(0, 0, MenuWidth, MenuHeight), menuPaperArt, ScaleMode.StretchToFill);
                if (menuPage != MenuPage.Home)
                    MenuFill(new Rect(0, 0, MenuWidth, MenuHeight), new Color(MenuPaper.r, MenuPaper.g, MenuPaper.b, 0.91f));
                DrawOperationsHeader();
                switch (menuPage)
                {
                    case MenuPage.Maps: DrawMapMenu(); break;
                    case MenuPage.Difficulty: DrawDifficultyMenu(); break;
                    case MenuPage.Options: DrawOptionsMenu(); break;
                    default: DrawHomeMenu(); break;
                }
            }
            finally { GUI.matrix = previousMatrix; }
        }

        void DrawOperationsHeader()
        {
            MenuFill(new Rect(48, 22, 394, 40), MenuYellow);
            GUI.Label(new Rect(65, 24, 319, 36), "OPERATIONS DESK", menuEyebrowStyle);
            for (int i = 0; i < 3; i++)
            {
                var matrix = GUI.matrix;
                // RotateAroundPivot wants the pivot in screen pixels, but the menu is drawn in its own 1600x900 space
                // and scaled to the window, so convert it through the current matrix. Passing the layout point
                // directly rotated the slashes around the wrong spot on any other resolution, and they drifted onto
                // the page title.
                Vector2 pivot = matrix.MultiplyPoint3x4(new Vector3(386 + i * 18, 42, 0f));
                GUIUtility.RotateAroundPivot(35f, pivot);
                MenuFill(new Rect(381 + i * 18, 26, 9, 32), MenuInk);
                GUI.matrix = matrix;
            }
            if (menuPage != MenuPage.Home)
            {
                GUI.Label(new Rect(1130, 31, 422, 30), "CRUSH HOUR  /  FIELD BRIEFING", menuSmallStyle);
                MenuFill(new Rect(48, 78, 1504, 2), MenuRule);
            }
        }

        void DrawHomeMenu()
        {
            GUI.Label(new Rect(43, 65, 735, 207), "CRUSH", menuHeroStyle);
            GUI.Label(new Rect(43, 248, 735, 207), "HOUR", menuHeroAccentStyle);
            GUI.Label(new Rect(51, 440, 670, 47), "Keep the crowd safe.", menuCardTitleStyle);
            MenuFill(new Rect(48, 499, 674, 2), MenuInk);
            if (DrawHomeCommand(new Rect(48, 526, 720, 112), "01", "START GAME", true))
                deferred.Add(() => menuPage = MenuPage.Maps);
            MenuFill(new Rect(48, 654, 674, 2), MenuInk);
            if (DrawHomeCommand(new Rect(48, 664, 674, 80), "02", "OPTIONS", false))
                deferred.Add(() => menuPage = MenuPage.Options);
            MenuFill(new Rect(48, 754, 674, 2), MenuInk);
            if (DrawHomeCommand(new Rect(48, 766, 674, 76), "03", "QUIT", false))
                deferred.Add(QuitFromMenu);
            MenuFill(new Rect(48, 858, 1504, 2), MenuInk);
            GUI.Label(new Rect(52, 865, 1030, 32), "3 MAPS    /    3 DIFFICULTIES    /    27 STARS", menuEyebrowStyle);
        }

        bool DrawHomeCommand(Rect rect, string index, string label, bool primary)
        {
            bool hovered = rect.Contains(Event.current.mousePosition);
            if (primary) MenuSlant(rect, hovered ? MenuHoverRed : MenuRed);
            else if (hovered) MenuFill(rect, new Color(MenuHoverPaper.r, MenuHoverPaper.g, MenuHoverPaper.b, 0.75f));
            Color divider = primary ? new Color(MenuPaper.r, MenuPaper.g, MenuPaper.b, 0.7f) : MenuRule;
            GUI.Label(new Rect(rect.x + 5, rect.y, 78, rect.height), index, primary ? menuWhiteIndexStyle : menuIndexStyle);
            MenuFill(new Rect(rect.x + 87, rect.y + 18, 2, rect.height - 36), divider);
            GUI.Label(new Rect(rect.x + 116, rect.y - 1, rect.width - 145, rect.height), label, primary ? menuHomeWhiteStyle : menuHomeActionStyle);
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        void DrawMapMenu()
        {
            GUI.Label(new Rect(48, 100, 1450, 84), "SELECT A SCENARIO", menuTitleStyle);
            GUI.Label(new Rect(51, 184, 1450, 33), "Choose a venue dossier. All three maps are available to command.", menuBodyStyle);
            EnsureMenuPreviews();
            // Same flow as the difficulty page: a card's button only selects it (red border and SELECTED badge), and
            // the red button at the bottom right moves on to the difficulty.
            for (int i = 0; i < MapCatalog.Maps.Count; i++)
            {
                var map = MapCatalog.Maps[i];
                var card = new Rect(48 + i * 512, 236, 480, 478);
                bool selected = map.Id == selectedMap;
                DrawMenuDossier(card, selected);
                var preview = new Rect(card.x + 20, card.y + 22, 440, 190);
                MenuFill(preview, new Color(0.88f, 0.88f, 0.83f, 0.75f));
                GUI.DrawTexture(preview, menuPreviews[i], ScaleMode.ScaleToFit);
                GUI.Label(new Rect(preview.x + 9, preview.y + 7, 175, 30), $"VENUE 0{i + 1}", menuEyebrowStyle);
                if (selected)
                {
                    MenuFill(new Rect(preview.xMax - 145, preview.y, 145, 34), MenuRed);
                    GUI.Label(new Rect(preview.xMax - 145, preview.y, 145, 34), "SELECTED", menuWhiteButtonStyle);
                }
                GUI.Label(new Rect(card.x + 22, card.y + 226, 438, 52), map.Title.ToUpperInvariant(), menuCardTitleStyle);
                GUI.Label(new Rect(card.x + 22, card.y + 280, 436, 70), map.Description, menuBodyStyle);
                for (int d = 0; d < 3; d++)
                {
                    float dx = card.x + 20 + d * 148;
                    GUI.Label(new Rect(dx, card.y + 356, 142, 26), ((Difficulty)d).ToString().ToUpperInvariant(), menuCenteredStyle);
                    DrawMenuStars(new Rect(dx, card.y + 382, 142, 33), MapProgress.GetStars(map.Id, (Difficulty)d), 26);
                }
                if (OperationsButton(new Rect(card.x + 24, card.y + 426, 432, 36), selected ? "VENUE SELECTED" : "SELECT VENUE", selected))
                {
                    VenueMap chosen = map.Id;
                    deferred.Add(() => selectedMap = chosen);
                }
            }
            GUI.Label(new Rect(315, 752, 710, 82),
                "Three stars per difficulty.\nYour best stars and score are saved for every venue.", menuSmallStyle);
            if (OperationsButton(new Rect(1072, 754, 480, 66), "NEXT  /  DIFFICULTY  >", true))
                deferred.Add(() => menuPage = MenuPage.Difficulty);
            DrawMenuBackButton();
        }

        void DrawDifficultyMenu()
        {
            GUI.Label(new Rect(48, 100, 1450, 84), "CHOOSE YOUR DIFFICULTY", menuTitleStyle);
            GUI.Label(new Rect(51, 184, 1450, 35), MapTitle + "  /  Set the challenge for this round.", menuBodyStyle);
            for (int i = 0; i < 3; i++)
            {
                Difficulty level = (Difficulty)i;
                var settings = DifficultySettings.For(level);
                var card = new Rect(48 + i * 512, 236, 480, 478);
                bool selected = level == difficulty;
                DrawMenuDossier(card, selected);
                GUI.Label(new Rect(card.x + 24, card.y + 20, 430, 52), level.ToString().ToUpperInvariant(), menuCardTitleStyle);
                if (selected)
                {
                    MenuFill(new Rect(card.x + 310, card.y + 22, 145, 34), MenuRed);
                    GUI.Label(new Rect(card.x + 310, card.y + 22, 145, 34), "SELECTED", menuWhiteButtonStyle);
                }
                GUI.Label(new Rect(card.x + 24, card.y + 79, 430, 109), settings.playerCount.ToString(), menuNumberStyle);
                GUI.Label(new Rect(card.x + 27, card.y + 181, 425, 31), "PEOPLE TO PROTECT", menuSmallStyle);
                MenuFill(new Rect(card.x + 24, card.y + 224, 432, 1), MenuRule);
                GUI.Label(new Rect(card.x + 24, card.y + 242, 432, 96),
                    $"{settings.patrolCount} patrols  /  {settings.gateCrews} gate crews\n{settings.disasterCount} disaster{(settings.disasterCount == 1 ? "" : "s")}  /  up to {settings.maxBlockedDoors} blocked exit{(settings.maxBlockedDoors == 1 ? "" : "s")}\n{settings.drunkFraction * 100f:0}% drunk  /  score x{settings.scoreMultiplier:0.#}", menuBodyStyle);
                DrawMenuStars(new Rect(card.x + 24, card.y + 343, 432, 36), MapProgress.GetStars(selectedMap, level), 31);
                GUI.Label(new Rect(card.x + 24, card.y + 387, 432, 27), $"BEST SCORE  {MapProgress.GetBestScore(selectedMap, level):N0}", menuCenteredStyle);
                if (OperationsButton(new Rect(card.x + 24, card.y + 426, 432, 36), selected ? "DIFFICULTY SELECTED" : "SELECT " + level.ToString().ToUpperInvariant(), selected))
                {
                    Difficulty chosen = level;
                    deferred.Add(() => difficulty = chosen);
                }
            }
            GUI.Label(new Rect(315, 752, 710, 82),
                DifficultySettings.StarSummary, menuSmallStyle);
            if (OperationsButton(new Rect(1072, 754, 480, 66), "START  /  " + difficulty.ToString().ToUpperInvariant() + "  >", true))
                deferred.Add(PlayFromMenu);
            DrawMenuBackButton();
        }

        void DrawOptionsMenu()
        {
            GUI.Label(new Rect(48, 100, 1450, 84), "COMMAND PREFERENCES", menuTitleStyle);
            GUI.Label(new Rect(51, 184, 1450, 35), "Adjust your view and camera controls. Preferences are saved automatically.", menuBodyStyle);
            DrawMenuDossier(new Rect(240, 250, 1120, 498), false);
            DrawMenuOption(280, "Zone boundaries", "Show the areas assigned to each patrol.", showRegions,
                () => { showRegions = !showRegions; SaveMenuPreferences(); });
            DrawMenuOption(386, "Pressure map", "Colour the ground where the crowd is under pressure.", heatmap,
                () => { heatmap = !heatmap; SaveMenuPreferences(); });
            DrawMenuOption(492, "Crowd state markers", "Show coloured tiles for panic, injury and escort needs.", stateMarkers,
                () => { stateMarkers = !stateMarkers; SaveMenuPreferences(); });
            MenuFill(new Rect(276, 598, 1048, 1), MenuRule);
            GUI.Label(new Rect(280, 622, 520, 47), "CAMERA SENSITIVITY", menuCardTitleStyle);
            GUI.Label(new Rect(281, 676, 520, 35), "Pan, rotate, drag and zoom speed.", menuSmallStyle);
            var previous = GUI.color;
            GUI.color = MenuInk;
            float adjusted = GUI.HorizontalSlider(new Rect(865, 663, 350, 32), cameraSensitivity, 0.5f, 2f, menuSliderStyle, menuThumbStyle);
            GUI.color = previous;
            if (!Mathf.Approximately(adjusted, cameraSensitivity))
            {
                cameraSensitivity = adjusted;
                cameraRig.inputSensitivity = cameraSensitivity;
                menuOptionsDirty = true;
            }
            GUI.Label(new Rect(1240, 639, 94, 50), $"{cameraSensitivity:0.0}x", menuCardTitleStyle);
            if (menuOptionsDirty && (Event.current.rawType == EventType.MouseUp || Event.current.rawType == EventType.KeyUp))
                deferred.Add(SaveMenuPreferences);
            DrawMenuBackButton();
            GUI.Label(new Rect(315, 843, 1150, 40), "Preferences apply immediately and remain saved for your next session.", menuSmallStyle);
        }

        void DrawMenuOption(float y, string title, string description, bool enabled, System.Action toggle)
        {
            GUI.Label(new Rect(280, y, 790, 46), title.ToUpperInvariant(), menuCardTitleStyle);
            GUI.Label(new Rect(281, y + 51, 790, 37), description, menuSmallStyle);
            if (OperationsButton(new Rect(1145, y + 12, 178, 58), enabled ? "ON" : "OFF", enabled))
                deferred.Add(toggle);
        }

        void DrawMenuBackButton()
        {
            if (OperationsButton(new Rect(48, 837, 222, 44), "<  BACK  [ESC]")) deferred.Add(MenuBack);
        }

        void DrawMenuDossier(Rect rect, bool selected)
        {
            MenuFill(new Rect(rect.x + 5, rect.y + 7, rect.width, rect.height), new Color(MenuInk.r, MenuInk.g, MenuInk.b, 0.08f));
            MenuFill(rect, new Color(0.98f, 0.957f, 0.9f, 0.93f));
            MenuBorder(rect, selected ? MenuRed : MenuRule, selected ? 3f : 1.5f);
            if (selected) MenuFill(new Rect(rect.x, rect.y, rect.width, 5), MenuRed);
        }

        bool OperationsButton(Rect rect, string label, bool primary = false)
        {
            bool hovered = rect.Contains(Event.current.mousePosition);
            if (primary)
                MenuSlant(rect, hovered ? MenuHoverRed : MenuRed);
            else
            {
                MenuFill(rect, hovered ? MenuHoverPaper : new Color(MenuPaper.r, MenuPaper.g, MenuPaper.b, 0.65f));
                MenuBorder(rect, MenuInk, hovered ? 2f : 1.5f);
            }
            GUI.Label(new Rect(rect.x + 7, rect.y, rect.width - 22, rect.height), label, primary ? menuWhiteButtonStyle : menuButtonLabelStyle);
            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        void MenuSlant(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, menuSlant);
            GUI.color = previous;
        }

        static void MenuBorder(Rect rect, Color color, float thickness = 1f)
        {
            MenuFill(new Rect(rect.x, rect.y, rect.width, thickness), color);
            MenuFill(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            MenuFill(new Rect(rect.x, rect.y, thickness, rect.height), color);
            MenuFill(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        static void MenuFill(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        static Texture2D CreateMenuSlant()
        {
            const int width = 256, height = 64;
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(width - (height - y) * 0.28f - x));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Operations Desk action tab",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        void EnsureMenuPreviews()
        {
            if (menuPreviews != null) return;
            menuPreviews = new Texture2D[MapCatalog.Maps.Count];
            for (int m = 0; m < menuPreviews.Length; m++)
            {
                var previewVenue = new FestivalVenue(MapCatalog.Maps[m].Id);
                var pixels = new Color32[FestivalVenue.CellCount];
                for (int y = 0; y < FestivalVenue.Height; y++)
                    for (int x = 0; x < FestivalVenue.Width; x++)
                    {
                        int i = FestivalVenue.Index(x, y);
                        CellType type = previewVenue.cells[i];
                        Color fill = type switch
                        {
                            CellType.Wall => new Color(0.2f, 0.27f, 0.37f),
                            CellType.Outside => new Color(0.9f, 0.89f, 0.83f),
                            CellType.Obstacle => new Color(0.4f, 0.47f, 0.56f),
                            CellType.Gate => MenuRed,
                            _ => new Color(0.94f, 0.935f, 0.88f),
                        };
                        if (type == CellType.Open && (x % 5 == 0 || y % 5 == 0))
                            fill = new Color(0.84f, 0.85f, 0.81f);
                        pixels[i] = fill;
                    }
                foreach (var landmark in previewVenue.landmarks)
                    foreach (var p in landmark.rect.allPositionsWithin)
                        if (FestivalVenue.InBounds(p.x, p.y))
                            pixels[FestivalVenue.Index(p.x, p.y)] = landmark.solid ? new Color(0.32f, 0.4f, 0.5f) : new Color(0.2f, 0.42f, 0.31f);
                var texture = new Texture2D(FestivalVenue.Width, FestivalVenue.Height, TextureFormat.RGBA32, false)
                {
                    name = "Operations venue plan " + MapCatalog.Maps[m].Id,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave,
                };
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                menuPreviews[m] = texture;
            }
        }

        void DrawMenuStars(Rect rect, int earned, float size = 24f)
        {
            if (menuStar == null) menuStar = CreateMenuStar();
            earned = Mathf.Clamp(earned, 0, 3);
            float gap = size * 0.2f;
            float x = rect.x + (rect.width - (size * 3f + gap * 2f)) * 0.5f;
            var previous = GUI.color;
            for (int i = 0; i < 3; i++)
            {
                GUI.color = i < earned ? new Color(0.83f, 0.57f, 0.08f) :
                    phase == Phase.Menu ? new Color(0.7f, 0.69f, 0.64f) : new Color(0.29f, 0.33f, 0.39f);
                GUI.DrawTexture(new Rect(x + i * (size + gap), rect.y + (rect.height - size) * 0.5f, size, size), menuStar);
            }
            GUI.color = previous;
        }

        static Texture2D CreateMenuStar()
        {
            const int size = 64;
            var points = new Vector2[10];
            for (int i = 0; i < points.Length; i++)
            {
                float angle = (90f + i * 36f) * Mathf.Deg2Rad;
                float radius = i % 2 == 0 ? 29f : 13f;
                points[i] = new Vector2(32f + Mathf.Cos(angle) * radius, 32f + Mathf.Sin(angle) * radius);
            }
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int samples = 0;
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                            if (InsideMenuStar(new Vector2(x + 0.25f + sx * 0.5f, y + 0.25f + sy * 0.5f), points)) samples++;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, samples * 0.25f);
                }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Menu star",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        static bool InsideMenuStar(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                if ((polygon[i].y > point.y) != (polygon[j].y > point.y) &&
                    point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) /
                    (polygon[j].y - polygon[i].y) + polygon[i].x) inside = !inside;
            return inside;
        }

        void DisposeMenuAssets()
        {
            if (menuStar != null) Destroy(menuStar);
            if (menuSlant != null) Destroy(menuSlant);
            if (menuPreviews != null)
                foreach (var texture in menuPreviews)
                    if (texture != null) Destroy(texture);
            menuStar = menuSlant = null;
            menuPreviews = null;
            // Resources textures and fonts are shared project assets, so leave them owned by Unity.
        }
    }
}
