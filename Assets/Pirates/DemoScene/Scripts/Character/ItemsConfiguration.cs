using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class ItemsConfiguration{

    [System.Serializable]
    public struct item
    {
        public Transform model;           // model that will be instantiated/switched on
        public List<Materials> materials; //materials assigned to model
    }
    
    public string displayName;             
    public Transform slot;               //slot at which items will be instantiated
    public List<item> items = new List<item>(); 

    private Transform currentItem;      //currently active object
    private int counter = 0;            //index of currently active object

    public void ActivateModel(int itemIndex)
    {
        if (itemIndex >= 0 && itemIndex < items.Count)
        {
            if (IsPresent(itemIndex))  //if object is present in scene we only switch it on
            {
                if (counter >= 0)
                    items[counter].model.gameObject.SetActive(false);

                items[itemIndex].model.gameObject.SetActive(true);

                counter = itemIndex;
                currentItem = items[itemIndex].model;
            }
            else                    //if object is a prefab we have to instantiate it
            {
                if (currentItem != null)
                    MonoBehaviour.Destroy(currentItem.gameObject);

                if (items[itemIndex].model != null && slot != null)
                    currentItem = MonoBehaviour.Instantiate(items[itemIndex].model, slot);

                counter = itemIndex;
            }
        }
        else
        {
            Debug.Log("Element index out of range. index: " + itemIndex + " | items count: " + items.Count + " | Name: " + displayName);
        }
    }

    public void ChangeMaterial(int itemIndex, int materialIndex)
    {
        //Our items can have either skinnedmeshrenderer component or meshrenderer component
        
        if (currentItem != null)
        {
            var skinnedMeshRenderer = currentItem.GetComponent<SkinnedMeshRenderer>();

            if (skinnedMeshRenderer == null)
                skinnedMeshRenderer = currentItem.GetComponentInChildren<SkinnedMeshRenderer>();

            if (skinnedMeshRenderer != null)
            {
                ChangeSkinnedMaterial(skinnedMeshRenderer, itemIndex, materialIndex);
            }
            else
            {
                var meshRenderer = currentItem.GetComponent<MeshRenderer>();

                if (meshRenderer == null)
                    meshRenderer = currentItem.GetComponentInChildren<MeshRenderer>();

                if(meshRenderer != null)
                    ChangeMeshMaterial(meshRenderer, itemIndex, materialIndex);
            }
        }
    }

    /// <summary>
    /// Changing material using MeshRenderer 
    /// </summary>
    private void ChangeMeshMaterial(MeshRenderer meshRenderer, int itemIndex, int materialIndex)
    {
            var meshMaterials = meshRenderer.materials; 

            meshMaterials[itemIndex] = GetMaterial(itemIndex, materialIndex);
            meshRenderer.materials = meshMaterials;
    }

    /// <summary>
    /// Changing material using SkinnedMeshRenderer 
    /// </summary>
    private void ChangeSkinnedMaterial(SkinnedMeshRenderer skinnedMeshRenderer, int itemIndex, int materialIndex)
    {
            var meshMaterials = skinnedMeshRenderer.materials;  

            meshMaterials[itemIndex] = GetMaterial(itemIndex, materialIndex);
            skinnedMeshRenderer.materials = meshMaterials;
    }

    private Material GetMaterial(int itemIndex, int materialIndex)
    {
        items[counter].materials[itemIndex].SetCounter = materialIndex;             //Updating counter of current material
        return items[counter].materials[itemIndex].aviableMaterials[materialIndex];
    }

    public void PlayAnimation(string animationName, float startTime)
    {
        if (currentItem != null)
        {
            var animations = currentItem.GetComponent<Animation>();

            if (animations != null)
            {
                var animationClips = new List<string>();
                foreach (AnimationState anim in animations)
                {
                    animationClips.Add(anim.name);
                }


                if (animationClips.Contains(animationName))
                {
                    animations[animationName].time = startTime;
                    animations.Play(animationName);
                }
            }
        }
    }

    /// <summary>
    /// Checking if item at give index is present in scene
    /// </summary>
    public bool IsPresent(int index)
    {
        if (items[index].model != null)
        {
            if (items[index].model.gameObject.scene.IsValid())
                return true;
        }

        return false;
    }

    public int GetCounter
    {
        get
        {
            return counter;
        }
    }

    public int SetCounter
    {
        set
        {
            counter = value;
        }
    }
}
