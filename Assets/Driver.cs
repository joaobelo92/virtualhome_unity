using System.Collections.Generic;
using StoryGenerator.Utilities;
using UnityEngine;

namespace StoryGenerator
{
    public class Driver : MonoBehaviour
    {
        
        public List<State> CurrentStateList = new List<State>();
        public int finishedChars = 0;

        public DataProviders dataProviders;
    }
}