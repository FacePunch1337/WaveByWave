using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PresetCharacter : MonoBehaviour {

    public List<Transform> characters;
    private Transform currentCharacter;

	public void ActivateCharacter(int index, Vector3 position)
    {
        if(index >= 0 && index < characters.Count)
        {
            DeactivatePreset();

            currentCharacter = Instantiate(characters[index], position, transform.rotation);

        }
    }

    public void DeactivatePreset()
    {
        if (currentCharacter != null)
            Destroy(currentCharacter.gameObject);
    }

    public Transform GetCurrentCharacter
    {
        get
        {
            return currentCharacter;
        }
    }
}
