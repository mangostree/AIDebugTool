using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ButtonDebugList : MonoBehaviour
{
	public static ButtonDebugList instance = null; 
	public DebugLogButton[] debugLogButtons = new DebugLogButton[0];
	// Start is called before the first frame update
	void Awake()
    {
        instance = this;
		debugLogButtons = transform.GetComponentsInChildren<DebugLogButton>();
	}

	public static int GetDebugButtonCount()
	{ 
		return instance.debugLogButtons.Length;
	}

	public static DebugLogButton GetDebugButtonById(int i)
	{
		if (i < 0 || i >= instance.debugLogButtons.Length)
		{
			return null;
		}
		return instance.debugLogButtons[i];
	}

	public static void ClickDebugButton(int id)
	{
		DebugLogButton btn = GetDebugButtonById(id);
		if (btn)
		{ 
			btn.button.onClick.Invoke();
		}
	}
}
