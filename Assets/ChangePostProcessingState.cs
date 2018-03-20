using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.PostProcessing;

public class ChangePostProcessingState : MonoBehaviour {
    public PostProcessingProfile Profile;

    // Use this for initialization
    void Start () {
		
	}
	
	// Update is called once per frame
	void Update () {
        if (Input.GetKeyDown("space"))
        {
            // check script is attached or not
            PostProcessingBehaviour temp = gameObject.GetComponent<PostProcessingBehaviour>();
            if (temp != null)
            {
                Debug.Log("post processing stack is attached.");
                Destroy(temp);
            }
            else
            {
                gameObject.AddComponent<PostProcessingBehaviour>();
                PostProcessingBehaviour behaviour = gameObject.GetComponent<PostProcessingBehaviour>();
                behaviour.profile = Profile;
            }
        }
	}
}
