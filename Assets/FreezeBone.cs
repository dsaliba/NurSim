using UnityEngine;

public class FreezeBone : MonoBehaviour
{
    Vector3 pos;
    Quaternion rot;

    void Start()
    {
        pos = transform.position;
        rot = transform.rotation;
    }

    void LateUpdate()
    {
        transform.position = pos; 
        transform.rotation = rot;
    }
}
