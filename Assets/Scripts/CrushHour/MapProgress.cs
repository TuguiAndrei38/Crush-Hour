using System;
using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    public enum VenueMap { SummerFestival = 0, CityStadium = 1, MetroConcourse = 2 }

    /// <summary>A playable venue, shared by the menu and the generated map.</summary>
    public sealed class MapDefinition
    {
        public VenueMap Id { get; }
        public string Title { get; }
        public string Description { get; }

        public MapDefinition(VenueMap id, string title, string description)
        {
            Id = id;
            Title = title;
            Description = description;
        }
    }

    public static class MapCatalog
    {
        public static IReadOnlyList<MapDefinition> Maps { get; } = Array.AsReadOnly(new[]
        {
            new MapDefinition(VenueMap.SummerFestival, "Summer Music Festival",
                "Guide fans past stages, food stalls and light towers to the emergency gates."),
            new MapDefinition(VenueMap.CityStadium, "City Stadium",
                "Clear the stands and concourse around the pitch through the stadium's exit lanes."),
            new MapDefinition(VenueMap.MetroConcourse, "Metro Concourse",
                "Lead commuters around platforms, kiosks and crowded concourse bottlenecks."),
        });

        public static VenueMap Normalize(VenueMap map) => (VenueMap)Mathf.Clamp((int)map, 0, Maps.Count - 1);

        public static MapDefinition Get(VenueMap map) => Maps[(int)Normalize(map)];
    }

    /// <summary>Best results for each map and difficulty. Every map is available from the start.</summary>
    public static class MapProgress
    {
        public static int GetStars(VenueMap map, Difficulty difficulty) =>
            Mathf.Clamp(PlayerPrefs.GetInt(Key(map, difficulty, "Stars"), 0), 0, 3);

        /// <summary>Returns zero until the first round; recorded scores may be negative.</summary>
        public static int GetBestScore(VenueMap map, Difficulty difficulty) =>
            PlayerPrefs.GetInt(Key(map, difficulty, "Score"), 0);

        public static void Record(VenueMap map, Difficulty difficulty, int stars, int score)
        {
            string starsKey = Key(map, difficulty, "Stars");
            string scoreKey = Key(map, difficulty, "Score");
            PlayerPrefs.SetInt(starsKey, Mathf.Max(GetStars(map, difficulty), Mathf.Clamp(stars, 0, 3)));
            // An absent score is different from zero: the first result can be below zero.
            if (!PlayerPrefs.HasKey(scoreKey) || score > PlayerPrefs.GetInt(scoreKey))
                PlayerPrefs.SetInt(scoreKey, score);
            PlayerPrefs.Save();
        }

        static string Key(VenueMap map, Difficulty difficulty, string result)
        {
            string mapId = MapCatalog.Normalize(map) switch
            {
                VenueMap.CityStadium => "CityStadium",
                VenueMap.MetroConcourse => "MetroConcourse",
                _ => "SummerFestival",
            };
            string difficultyId = (Difficulty)Mathf.Clamp((int)difficulty, 0, 2) switch
            {
                Difficulty.Easy => "Easy",
                Difficulty.Hard => "Hard",
                _ => "Medium",
            };
            return $"CrushHour.Progress.{mapId}.{difficultyId}.{result}";
        }
    }
}
