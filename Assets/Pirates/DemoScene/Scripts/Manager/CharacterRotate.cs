using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CharacterRotate : MonoBehaviour {

    public Transform objectToRotate;
    public float rotationSpeed = 1;

    private void Update()
    {
        if (CheckMouseInput())
            Rotation();
    }

    private bool CheckMouseInput()
    {
        if (Input.GetMouseButton(0))
            return true;

        return false;
    }

    private void Rotation()
    {
        var xInput = Input.GetAxisRaw("Mouse X");

        if (xInput != 0)
            XRotateObject(xInput * -rotationSpeed);

    }

    private void XRotateObject(float input)
    {
        var vector = Vector3.zero;
        vector.y += input;
        objectToRotate.Rotate(vector);
    }
}
