using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class OptionsHolder : MonoBehaviour {

    public Dropdown assetList;
    public Button previousButton;
    public Button nextButton;


    public void CheckButtons()
    {
        if(assetList.value > 0)
            previousButton.interactable = true;
        else
            previousButton.interactable = false;

        if (assetList.value < assetList.options.Count - 1)
            nextButton.interactable = true;
        else
            nextButton.interactable = false;
    }
}
