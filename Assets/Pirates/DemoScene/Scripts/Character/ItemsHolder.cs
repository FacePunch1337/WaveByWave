using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ItemsHolder : MonoBehaviour {

    public List<ItemsConfiguration> itemsConfiguration = new List<ItemsConfiguration>();

    private void Awake()
    {
        for(int i = 0; i < itemsConfiguration.Count; i++)
        {
            //Adding "None" option for items that will be instantiated
            if (itemsConfiguration[i].IsPresent(0).Equals(false))
            {
                var items = new List<ItemsConfiguration.item>();
                var nullItem = new ItemsConfiguration.item { model = null, materials = null };
                items.Add(nullItem);
                items.AddRange(itemsConfiguration[i].items);
                itemsConfiguration[i].items = items;
                itemsConfiguration[i].SetCounter = 0;
            }
        }
    }
}
