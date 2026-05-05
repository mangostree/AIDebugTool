using UnityEngine;

public class CursorBreakpointDebugProbe : MonoBehaviour
{
    private void Start()
    {
        int randomValue = Random.Range(100000, 999999);
        Debug.Log($"[CursorBreakpointDebugProbe] randomValue={randomValue}");
    }
}
