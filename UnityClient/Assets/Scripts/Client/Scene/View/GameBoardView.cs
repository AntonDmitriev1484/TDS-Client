using Assets.Scripts.Utilities;
using SharedLibrary.Game;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

/// <summary>
/// Draws the board, each player's goal, and each player's score as described by the shared game
/// state. Nothing here is interactive: these are only visuals, and every change to them comes from
/// the server. Scores sit at the midpoint of the board edge nearest that player's goal, i.e. their
/// end of the table, and always turn to face this client's camera.
/// </summary>
public class GameBoardView : MonoBehaviour
{
    // Sprites/Default is always included in builds, unlike the Standard shader's transparent variants.
    const string TransparentShaderName = "Sprites/Default";

    [Header("Scores")]
    [Tooltip("Score text size (TextMeshPro font size; about 0.1 m per unit).")]
    [SerializeField] float _scoreFontSize = 0.5f;

    [Tooltip("How far above the board's top face the score text's center is (meters).")]
    [SerializeField] float _scoreHeightAboveBoard = 0.05f;

    Renderer _board;
    readonly Dictionary<int, Renderer> _goals = new Dictionary<int, Renderer>();
    readonly Dictionary<int, TextMeshPro> _scores = new Dictionary<int, TextMeshPro>();

    void LateUpdate()
    {
        Camera head = Camera.main;
        if (head == null)
            return;

        foreach (TextMeshPro score in _scores.Values)
        {
            // Text reads correctly when its forward points away from the viewer.
            Vector3 away = score.transform.position - head.transform.position;
            if (away.sqrMagnitude > 1e-6f)
                score.transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }

    /// <summary>
    /// Creates, moves, recolors or removes the visuals to match <paramref name="board"/>,
    /// <paramref name="goals"/> and <paramref name="scores"/> (player ID to score).
    /// </summary>
    public void Apply(BoardState board, IEnumerable<GoalCubeState> goals, IReadOnlyDictionary<int, int> scores)
    {
        ApplyBoardAndGoals(board, goals);
        ApplyScores(board, goals, scores);
    }

    void ApplyBoardAndGoals(BoardState board, IEnumerable<GoalCubeState> goals)
    {
        if (board != null)
        {
            if (_board == null)
                _board = CreateVisual("Board", transparent: false);

            Place(_board, board.Pose, Conversions.ToUnityVector(board.BoardSize), board.Color);
        }
        else if (_board != null)
        {
            Destroy(_board.gameObject);
            _board = null;
        }

        var seen = new HashSet<int>();
        foreach (GoalCubeState goal in goals)
        {
            seen.Add(goal.PlayerID);
            if (!_goals.TryGetValue(goal.PlayerID, out Renderer visual))
            {
                visual = CreateVisual($"Goal (player {goal.PlayerID})", transparent: true);
                _goals.Add(goal.PlayerID, visual);
            }

            Place(visual, goal.Pose, Vector3.one * goal.CubeSize, goal.Color);
        }

        foreach (int playerID in new List<int>(_goals.Keys))
        {
            if (seen.Contains(playerID))
                continue;

            Destroy(_goals[playerID].gameObject);
            _goals.Remove(playerID);
        }
    }

    /// <summary>One score per goal, at that goal's player's end of the board.</summary>
    void ApplyScores(BoardState board, IEnumerable<GoalCubeState> goals, IReadOnlyDictionary<int, int> scores)
    {
        var seen = new HashSet<int>();
        if (board != null)
        {
            foreach (GoalCubeState goal in goals)
            {
                seen.Add(goal.PlayerID);
                if (!_scores.TryGetValue(goal.PlayerID, out TextMeshPro text))
                {
                    text = CreateScoreText(goal.PlayerID);
                    _scores.Add(goal.PlayerID, text);
                }

                scores.TryGetValue(goal.PlayerID, out int score);
                text.text = $"Player {goal.PlayerID}: {score}";
                // The player's goal color, opaque so the text stays readable over the semi-transparent goals.
                text.color = new Color(goal.Color.R, goal.Color.G, goal.Color.B, 1f);
                text.transform.position = GetPlayerEndOfBoard(board, Conversions.ToUnityPosition(goal.Pose));
            }
        }

        foreach (int playerID in new List<int>(_scores.Keys))
        {
            if (seen.Contains(playerID))
                continue;

            Destroy(_scores[playerID].gameObject);
            _scores.Remove(playerID);
        }
    }

    TextMeshPro CreateScoreText(int playerID)
    {
        var go = new GameObject($"Score (player {playerID})");
        var text = go.AddComponent<TextMeshPro>();
        text.fontSize = _scoreFontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.rectTransform.sizeDelta = new Vector2(1f, 0.2f);
        // Black until the player's goal from the authoritative game state colors it.
        text.color = Color.black;
        return text;
    }

    /// <summary>
    /// The midpoint of the board edge nearest <paramref name="playerGoalPosition"/>, raised just
    /// above the board's top face. Goals sit above their players, so this is that player's end.
    /// </summary>
    Vector3 GetPlayerEndOfBoard(BoardState board, Vector3 playerGoalPosition)
    {
        Vector3 center = Conversions.ToUnityPosition(board.Pose);
        Quaternion rotation = Conversions.ToUnityRotation(board.Pose);
        Vector3 size = Conversions.ToUnityVector(board.BoardSize);

        // Compare in the board's frame, relative to its size, so the nearer of its four edges wins.
        Vector3 local = Quaternion.Inverse(rotation) * (playerGoalPosition - center);
        Vector3 edge = Mathf.Abs(local.x) / size.x > Mathf.Abs(local.z) / size.z
            ? new Vector3(Mathf.Sign(local.x) * size.x * 0.5f, 0f, 0f)
            : new Vector3(0f, 0f, Mathf.Sign(local.z) * size.z * 0.5f);
        edge.y = size.y * 0.5f + _scoreHeightAboveBoard;

        return center + rotation * edge;
    }

    Renderer CreateVisual(string name, bool transparent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;

        // Visual only: keep it out of hand proximity tests and physics.
        Destroy(go.GetComponent<Collider>());

        var renderer = go.GetComponent<Renderer>();
        if (transparent)
        {
            Shader shader = Shader.Find(TransparentShaderName);
            if (shader != null)
                renderer.material = new Material(shader);
            else
                Debug.LogWarning($"{nameof(GameBoardView)}: shader '{TransparentShaderName}' not found; {name} will be opaque.");
        }

        return renderer;
    }

    static void Place(Renderer visual, SharedLibrary.Game.Actions.Pose pose, Vector3 scale, RGBAColor color)
    {
        visual.transform.SetPositionAndRotation(Conversions.ToUnityPosition(pose), Conversions.ToUnityRotation(pose));
        visual.transform.localScale = scale;
        visual.material.color = new Color(color.R, color.G, color.B, color.A);
    }
}
