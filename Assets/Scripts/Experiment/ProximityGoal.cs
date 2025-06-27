using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ProximityGoal : TrialGoal
{
    //public int robotIndex = 0;
    public double radius = 1;

    public GameObject fufiller;
    
    
    // Start is called before the first frame update
    void Start()
    {
        gameObject.SetActive(false);
        //robot = TaskEnvironment.instances[TaskEnvironment.currentIndex].getObjectListByKey("robots")[robotIndex];
    }

    // Update is called once per frame
    void Update()
    {
        if (gameObject.activeSelf)
        {
            double distance = Vector3.Distance(fufiller.transform.position, transform.position);
            if (distance < radius)
            {
                Complete();
                gameObject.SetActive(false);
            }
        }
    }

    public override void Activate()
    {
        gameObject.SetActive(true);
    }
}
