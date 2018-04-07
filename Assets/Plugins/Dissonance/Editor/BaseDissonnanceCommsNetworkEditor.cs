using System;
using Dissonance.Networking;
using UnityEditor;
using UnityEngine;

namespace Dissonance.Editor
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TComms"></typeparam>
    /// <typeparam name="TServer"></typeparam>
    /// <typeparam name="TClient"></typeparam>
    /// <typeparam name="TPeer"></typeparam>
    /// <typeparam name="TClientParam"></typeparam>
    /// <typeparam name="TServerParam"></typeparam>
    public class BaseDissonnanceCommsNetworkEditor<TComms, TServer, TClient, TPeer, TClientParam, TServerParam>
        : UnityEditor.Editor
        where TComms : BaseCommsNetwork<TServer, TClient, TPeer, TClientParam, TServerParam>
        where TServer : BaseServer<TServer, TClient, TPeer>
        where TClient : BaseClient<TServer, TClient, TPeer>
        where TPeer : struct, IEquatable<TPeer>
    {
        private Texture2D _logo;

        public void Awake()
        {
            _logo = Resources.Load<Texture2D>("dissonance_logo");
        }

        public override void OnInspectorGUI()
        {
            GUILayout.Label(_logo);

            var network = (TComms)target;

            if (Application.isPlaying)
            {
                GUILayout.Label("Network Stats");

                var indent = EditorGUI.indentLevel;
                EditorGUI.indentLevel++;
                try
                {
                    network.OnInspectorGui();
                    EditorUtility.SetDirty(network);
                }
                finally
                {
                    EditorGUI.indentLevel = indent;
                }
            }
        }
    }
}
