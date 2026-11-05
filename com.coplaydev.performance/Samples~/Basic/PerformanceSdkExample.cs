using Coplaydev.Performance;
using UnityEngine;

public sealed class PerformanceSdkExample : MonoBehaviour
{
    private void Start()
    {
        Performance.SetPage("Lobby");
    }

    public void OnEnterBattle()
    {
        Performance.SetPage("BattleHUD");
    }
}
