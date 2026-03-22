using System.Collections.Generic;
using UnityEngine;

public class MuseumDataManager : MonoBehaviour
{
    private static MuseumDataManager _instance;

    public static MuseumDataManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<MuseumDataManager>();
                if (_instance == null)
                {
                    var obj = new GameObject("MuseumDataManager");
                    _instance = obj.AddComponent<MuseumDataManager>();
                }
            }
            return _instance;
        }
    }

    private MuseumData museumData;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        LoadMuseumData();
    }

    private void LoadMuseumData()
    {
        TextAsset jsonFile = Resources.Load<TextAsset>("museumData");
        if (jsonFile != null)
        {
            museumData = JsonUtility.FromJson<MuseumData>(jsonFile.text);
            Debug.Log($"Loaded {museumData.exhibits.Count} exhibits from local database");
        }
        else
        {
            Debug.LogError("museumData.json not found in Resources folder!");
            museumData = new MuseumData { exhibits = new List<Exhibit>() };
        }
    }

    public MuseumData GetData() => museumData;

    public List<Exhibit> GetExhibits() => museumData?.exhibits ?? new List<Exhibit>();

    public string BuildExhibitContext()
    {
        if (museumData?.exhibits == null || museumData.exhibits.Count == 0)
            return "";

        var context = new System.Text.StringBuilder();
        context.AppendLine("=== Museum Exhibits Context ===\n");

        foreach (var exhibit in museumData.exhibits)
        {
            context.Append($"Title: {exhibit.title}");
            if (!string.IsNullOrEmpty(exhibit.location))
                context.Append($" | Location: {exhibit.location}");
            if (!string.IsNullOrEmpty(exhibit.creator))
                context.Append($" | Creator: {exhibit.creator}");
            if (!string.IsNullOrEmpty(exhibit.origin))
                context.Append($" | Origin: {exhibit.origin}");
            if (!string.IsNullOrEmpty(exhibit.media))
                context.Append($" | Media: {exhibit.media}");
            context.AppendLine($"\nDetails: {exhibit.details}\n");
        }

        return context.ToString();
    }

    [System.Serializable]
    public class MuseumData
    {
        public List<Exhibit> exhibits = new List<Exhibit>();
    }

    [System.Serializable]
    public class Exhibit
    {
        public string title;
        public string details;
        public string location;
        public string creator;
        public string origin;
        public string media;
    }
}
