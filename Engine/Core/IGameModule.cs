namespace Wisko.Engine.Core;

public interface IGameModule
{
    void OnInitialize();
    void OnUpdate(float deltaTime);
    void OnShutdown();
}
