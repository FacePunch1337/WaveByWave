using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class Materials{

    public List<Material> aviableMaterials = new List<Material>();
    private int counter;

    public List<Material> GetMaterials
    {
        get
        {
            return aviableMaterials;
        }
    }

    public int SetCounter
    {
        set
        {
            counter = value;
        }
    }

    public int GetCounter
    {
        get
        {
            return counter;
        }
    }
}
