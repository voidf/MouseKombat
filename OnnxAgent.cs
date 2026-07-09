using System;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MouseKombat.Sim;

// In-game RL policy driver: loads a trained .onnx (exported by rl/export_onnx.py) and turns the
// sim observation into an InputFrame each tick. Implements the sim's IAgent so it plugs into the
// same slot as the state-machine AI. Runs headless-fast; inference is a tiny MLP.
//
// Contract (matches export_onnx.py + mk_env.py):
//   input  = Observation.Get (32 floats)
//   output = 10 logits [L,R,U,D, LP,MP,HP,LK,MK,HK]; pressed = logit > 0.
//   dirs are held; buttons are edge-detected into the just-pressed mask (like a real press).
public sealed class OnnxAgent : IAgent, IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly bool[] _prevBtn = new bool[6];
    private readonly float[] _obs = new float[Observation.Size];

    public OnnxAgent(string osModelPath)
    {
        _session = new InferenceSession(osModelPath);
        foreach (var i in _session.InputMetadata) { _inputName = i.Key; break; }
    }

    public InputFrame Decide(GameSim sim, int selfIndex)
    {
        Observation.Fill(sim, selfIndex, 800f, 600f, _obs);

        var input = new DenseTensor<float>(_obs, new[] { 1, Observation.Size });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var logits = results[0].AsTensor<float>();

        bool left = logits[0, 0] > 0f;
        bool right = logits[0, 1] > 0f;
        bool up = logits[0, 2] > 0f;
        bool down = logits[0, 3] > 0f;

        int mask = 0;
        for (int i = 0; i < 6; i++)
        {
            bool pressed = logits[0, 4 + i] > 0f;
            if (pressed && !_prevBtn[i]) mask |= 1 << i; // rising edge = a press
            _prevBtn[i] = pressed;
        }

        return new InputFrame(left, right, up, down, mask);
    }

    public void Dispose() => _session?.Dispose();
}
