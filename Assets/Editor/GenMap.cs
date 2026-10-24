// Legacy stub replaced by MapGeneratorWindow.
// Open via menu: GooseGoose > Map Generator
using UnityEditor;

public static class GenMap
{
    [MenuItem("GooseGoose/Open Map Generator (GenMap)")]
    public static void Open()
    {
        MapGeneratorWindow.Open();
    }
}
