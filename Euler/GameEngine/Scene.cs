namespace Euler.GameEngine;

public abstract class Scene
{
    public Scene(string name)
    {
        Engine.Scenes.Add(name, this);
    }

    public virtual void Start() {}
    public virtual void Update() {}
    public virtual void Draw() {}
}