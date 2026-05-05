using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DebugLogButton : MonoBehaviour
{
    public Button button;
    public string logMsg = "";

    // Start is called before the first frame update
    void Start()
    {
		button = GetComponent<Button>();
		button.onClick.AddListener(OnClick);
	}

    void OnClick()
    {
        Debug.Log(logMsg);
    }
}
