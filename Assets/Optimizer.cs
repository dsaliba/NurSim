using System.Collections.Generic;
using UnityEngine;

public class Optimizer : MonoBehaviour
{
    [Tooltip("Names of GameObjects whose DIRECT children named 'Visuals' or 'Collisions' will not be disabled.")]
    public List<string> exceptionList = new List<string>();

    void Start()
    {
        TraverseAndDisable(transform, isUnderException: false);
    }

    void TraverseAndDisable(Transform current, bool isUnderException)
    {
        bool isCurrentException = exceptionList.Contains(current.name);

        foreach (Transform child in current)
        {
            // If this child is named "Visuals" or "Collisions"
            if (child.name == "Visuals" || child.name == "Collisions")
            {
                // Disable if not directly under an exception
                if (!isUnderException)
                {
                    child.gameObject.SetActive(false);
                }
            }

            // Recurse into children
            TraverseAndDisable(child, isUnderException: isCurrentException);
        }
    }
}
