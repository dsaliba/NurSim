//Do not edit! This file was generated by Unity-ROS MessageGeneration.
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;

namespace RosMessageTypes.KortexDriver
{
    [Serializable]
    public class JointAnglesMsg : Message
    {
        public const string k_RosMessageName = "kortex_driver/JointAngles";
        public override string RosMessageName => k_RosMessageName;

        public JointAngleMsg[] joint_angles;

        public JointAnglesMsg()
        {
            this.joint_angles = new JointAngleMsg[0];
        }

        public JointAnglesMsg(JointAngleMsg[] joint_angles)
        {
            this.joint_angles = joint_angles;
        }

        public static JointAnglesMsg Deserialize(MessageDeserializer deserializer) => new JointAnglesMsg(deserializer);

        private JointAnglesMsg(MessageDeserializer deserializer)
        {
            deserializer.Read(out this.joint_angles, JointAngleMsg.Deserialize, deserializer.ReadLength());
        }

        public override void SerializeTo(MessageSerializer serializer)
        {
            serializer.WriteLength(this.joint_angles);
            serializer.Write(this.joint_angles);
        }

        public override string ToString()
        {
            return "JointAnglesMsg: " +
            "\njoint_angles: " + System.String.Join(", ", joint_angles.ToList());
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#else
        [UnityEngine.RuntimeInitializeOnLoadMethod]
#endif
        public static void Register()
        {
            MessageRegistry.Register(k_RosMessageName, Deserialize);
        }
    }
}
