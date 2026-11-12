using UnityEditor;

/// <summary>
/// The control room models (Assets/Models/ControlRoom) are imported with Read/Write on: clicks through the screens
/// need the screen triangles and UVs at runtime (ControlRoom.Screens.cs). Bumping the version reimports them.
/// </summary>
class ControlRoomModelImport : AssetPostprocessor
{
    const string Folder = "Assets/Models/ControlRoom/";

    public override uint GetVersion() => 1;

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Folder)) return;
        var importer = (ModelImporter)assetImporter;
        importer.isReadable = true;
    }
}
