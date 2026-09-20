using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraMovement : MonoBehaviour {

    public Transform mainCamera;
    public float zoomSpeed = 1;

    private void Update()
    {
        ZoomInput();
        if (Input.GetMouseButton(2))
            YRotateInput();
    }

    private void YRotateInput()
    {
        var yInput = Input.GetAxisRaw("Mouse Y");

        if (yInput != 0)
            YRotateObject(yInput * -1);
    }

    private void ZoomInput()
    {
        var input = Input.GetAxisRaw("Mouse ScrollWheel");

        if (input != 0)
            Zoom(input * -1);
    }

    private void Zoom(float input)
    {
        var vector = mainCamera.position;
        vector.z += input;
        mainCamera.position = vector;
    }

    private void YRotateObject(float input)
    {
        var vector = Vector3.zero;
        vector.x += input;
        mainCamera.Rotate(vector);
    }
}
