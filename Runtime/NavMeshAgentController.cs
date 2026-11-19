using System;
using System.Collections;
using System.Threading;
using CupkekGames.TimeSystem;
using Cysharp.Threading.Tasks;
using PrimeTween;
using UnityEngine;
using UnityEngine.AI;
using CupkekGames.Animations;
using Unity.Scripting.LifecycleManagement;

namespace CupkekGames.Navigation
{
  /// <summary>
  /// A <see cref="NavMeshAgent"/> with its locomotion: what the rig plays
  /// follows how fast the agent really goes (its move kind while it moves,
  /// Idle while it stands, played only when that changes, so a looping clip
  /// is never restarted and a one-shot is never stomped), an optional eased
  /// turn toward where it goes, and an arrival event once per destination
  /// given through <see cref="MoveTo"/>. A one-shot owns the rig through
  /// <see cref="HoldLocomotion"/>; locomotion takes it back as the hold ends.
  /// </summary>
  [RequireComponent(typeof(NavMeshAgent))]
  public partial class NavMeshAgentController : MonoBehaviour
  {
    // Static variables
    [NoAutoStaticsCleanup]
    public static float MonitorFrequency = 0.1f;

    // Steering
    [NoAutoStaticsCleanup]
    public static float Speed = 1f;
    [NoAutoStaticsCleanup]
    public static float AngularSpeed = 3200f;
    [NoAutoStaticsCleanup]
    public static float Acceleration = 8f;

    [NoAutoStaticsCleanup]
    public static float StoppingDistance = .2f;

    // The kind played while the agent moves: Walk, or a rig's own moving
    // kind (a unit that runs to its targets plays its Run).
    [Header("Locomotion")]
    [Tooltip("The animation kind played while the agent moves.")]
    [SerializeField] private string _moveAnimationKind = AnimationKinds.Walk;
    [Tooltip("A standing agent starts its move kind above this speed, in m/s.")]
    [SerializeField] private float _startMovingSpeed = 0.5f;
    [Tooltip("A moving agent goes back to Idle below this speed, in m/s...")]
    [SerializeField] private float _stopMovingSpeed = 0.2f;
    [Tooltip("...once it has been below it this long, in seconds (a corner or a bump never flickers it to Idle).")]
    [SerializeField] private float _stopDelay = 0.12f;
    [Tooltip("0: the agent turns itself. Above 0: the controller eases the facing toward where the agent really goes, this sharp (about 12 is brisk).")]
    [SerializeField] private float _turnSharpness;

    /// <summary>The animation kind played while the agent moves (Walk unless the rig says otherwise). A change plays at once while it moves.</summary>
    public string MoveAnimationKind
    {
      get => _moveAnimationKind;
      set
      {
        if (_moveAnimationKind == value) return;
        _moveAnimationKind = value;
        if (_playedKind != AnimationKinds.Idle) _playedKind = null;
      }
    }

    /// <summary>How sharply the controller turns the agent toward where it goes; 0 hands turning back to the agent.</summary>
    public float TurnSharpness
    {
      get => _turnSharpness;
      set
      {
        _turnSharpness = Mathf.Max(0f, value);
        if (_navMeshAgent != null) _navMeshAgent.updateRotation = _turnSharpness <= 0f;
      }
    }

    // References
    private NavMeshAgent _navMeshAgent;
    public NavMeshAgent NavMeshAgent => _navMeshAgent;
    private IAnimationStateController _animationController;
    private AnimationClip _idleClip;

    public IAnimationStateController AnimationController => _animationController;

    // Locomotion: driven or not, the kind played last (null: it plays again
    // on the next frame), since when a moving agent has been slow, the holds
    // on it, and whether a destination given through MoveTo is on its way.
    private bool _driving;
    private string _playedKind;
    private float _slowSince = -1f;
    private int _holds;
    private bool _hasDestination;

    // Events
    /// <summary>The destination given through <see cref="MoveTo"/> is reached: raised once per destination.</summary>
    public event Action OnDestinationReached;

    // Follow
    private Transform _follow;
    private Coroutine _followCoroutine;
    private bool _followingActive = false;

    private float _stopDistance => _navMeshAgent.stoppingDistance;

    // Speed: the agent's own top speed (the time scale and a root act on it).
    private float _baseSpeed;
    private float _timeScale = 1f;
    private bool _rooted;
    // Paused by a time scale of zero: what was running comes back with time.
    private bool _paused;
    private bool _followBeforePause;
    private bool _agentBeforePause;

    // Other
    private Tween _dashTween;

    private Tween _tweenLookAt;

    // Time Context
    private TimeBundle _timeBundle;

    private void Awake()
    {
      _navMeshAgent = GetComponent<NavMeshAgent>();

      _navMeshAgent.enabled = false;

      _baseSpeed = Speed;
      _navMeshAgent.speed = Speed;
      _navMeshAgent.angularSpeed = AngularSpeed;
      _navMeshAgent.acceleration = Acceleration;
      _navMeshAgent.stoppingDistance = StoppingDistance;
      _navMeshAgent.updateRotation = _turnSharpness <= 0f;

      SetAnimationController(GetComponentInChildren<IAnimationStateController>());
    }

    private void OnDestroy()
    {
      if (_animationController != null) _animationController.OnAnimationPlayed -= OnClipPlayed;
    }

    /// <summary>The rig locomotion plays on (a model swapped in after Awake).</summary>
    public void SetAnimationController(IAnimationStateController controller)
    {
      if (_animationController != null) _animationController.OnAnimationPlayed -= OnClipPlayed;
      _animationController = controller;
      _idleClip = null;
      _playedKind = null;
      if (_animationController == null) return;
      _animationController.TryGetClip(AnimationKinds.Idle, out _idleClip);
      _animationController.OnAnimationPlayed += OnClipPlayed;
    }

    /// <summary>The agent's own top speed; a time scale and a root act on it.</summary>
    public float BaseSpeed
    {
      get => _baseSpeed;
      set
      {
        _baseSpeed = Mathf.Max(0f, value);
        ApplySpeed();
      }
    }

    // ── Locomotion ────────────────────────────────────────────────────────

    /// <summary>Locomotion drives the rig from now on (its move kind and Idle by the agent's speed).</summary>
    public void StartLocomotion()
    {
      _driving = true;
      _playedKind = null;
    }

    /// <summary>Locomotion lets the rig be (another system owns it for good).</summary>
    public void StopLocomotion()
    {
      _driving = false;
      _playedKind = null;
      _slowSince = -1f;
    }

    /// <summary>Whether locomotion drives the rig.</summary>
    public bool DrivesLocomotion => _driving;

    /// <summary>
    /// A one-shot owns the rig (a gather, a throw, sitting) while the hold
    /// lives; holds count, and as the last ends locomotion plays what the
    /// agent really does at once. Dispose it to release.
    /// </summary>
    public IDisposable HoldLocomotion()
    {
      _holds++;
      _playedKind = null;
      return new LocomotionHold(this);
    }

    /// <summary>Whether a one-shot holds the rig now.</summary>
    public bool LocomotionHeld => _holds > 0;

    private void ReleaseHold()
    {
      if (_holds <= 0) return;
      _holds--;
      _playedKind = null;
      _slowSince = -1f;
    }

    private sealed class LocomotionHold : IDisposable
    {
      private NavMeshAgentController _owner;

      public LocomotionHold(NavMeshAgentController owner) => _owner = owner;

      public void Dispose()
      {
        if (_owner != null) _owner.ReleaseHold();
        _owner = null;
      }
    }

    /// <summary>The agent walks to <paramref name="destination"/>; <see cref="OnDestinationReached"/> is raised once as it gets there.</summary>
    public void MoveTo(Vector3 destination)
    {
      if (!_navMeshAgent.isActiveAndEnabled || !_navMeshAgent.isOnNavMesh) return;
      _navMeshAgent.SetDestination(destination);
      _hasDestination = true;
    }

    /// <summary>The walk to a destination is called off: no arrival is raised for it.</summary>
    public void ClearDestination()
    {
      if (_navMeshAgent.isActiveAndEnabled && _navMeshAgent.isOnNavMesh && _navMeshAgent.hasPath) _navMeshAgent.ResetPath();
      _hasDestination = false;
    }

    private void Update()
    {
      if (_paused) return;

      if (_navMeshAgent.isActiveAndEnabled)
      {
        CheckArrival();
        Turn();
      }

      if (_driving && _holds == 0) Animate();
    }

    private void CheckArrival()
    {
      if (!_hasDestination || _navMeshAgent.pathPending) return;
      if (_navMeshAgent.hasPath && _navMeshAgent.remainingDistance > _stopDistance + 0.05f) return;
      _hasDestination = false;
      OnDestinationReached?.Invoke();
    }

    private void Turn()
    {
      if (_turnSharpness <= 0f) return;
      Vector3 velocity = _navMeshAgent.velocity;
      velocity.y = 0f;
      if (velocity.sqrMagnitude < 0.09f) return;
      Quaternion facing = Quaternion.LookRotation(velocity);
      transform.rotation = Quaternion.Slerp(transform.rotation, facing, 1f - Mathf.Exp(-_turnSharpness * Time.deltaTime));
    }

    // The rig plays what the agent really does, only when that changes.
    private void Animate()
    {
      if (_animationController == null) return;

      Vector3 velocity = _navMeshAgent.isActiveAndEnabled ? _navMeshAgent.velocity : Vector3.zero;
      velocity.y = 0f;
      float speed = velocity.magnitude;
      bool walking = _playedKind != null && _playedKind != AnimationKinds.Idle;
      bool moving = speed > (walking ? _stopMovingSpeed : _startMovingSpeed);
      if (walking && !moving)
      {
        if (_slowSince < 0f) _slowSince = Time.time;
        if (Time.time - _slowSince < _stopDelay) moving = true;
      }
      else _slowSince = -1f;

      string kind = moving ? MoveKindOnRig() : AnimationKinds.Idle;
      if (kind == _playedKind) return;
      _playedKind = kind;
      _animationController.Play(kind);
    }

    // The move kind this rig can play: a rig without it (no Run of its own)
    // walks, never sliding on the pose it held.
    private string MoveKindOnRig()
      => _moveAnimationKind == AnimationKinds.Walk || _animationController.TryGetClip(_moveAnimationKind, out _)
        ? _moveAnimationKind
        : AnimationKinds.Walk;

    // Idle came on by itself while the agent moves (a one-shot's end): play
    // what it really does on the next frame.
    private void OnClipPlayed(AnimationClip clip)
    {
      if (clip != null && clip == _idleClip && _playedKind != null && _playedKind != AnimationKinds.Idle) _playedKind = null;
    }

    // ── Follow ────────────────────────────────────────────────────────────

    public void FollowTarget(Transform follow, bool forceAnimation, bool debug)
    {
      if (_followCoroutine != null && follow == _follow)
      {
        if (debug)
        {
          Debug.Log("Already following this target: " + follow.name);
        }

        return;
      }

      StopFollow();
      if (follow == null) return;

      _follow = follow;
      _followingActive = true;

      LookAtTarget(follow);

      if (MakeSureOnNavMesh())
      {
        _followCoroutine = StartCoroutine(FollowTargetCoroutine(follow, debug));
      }

      // Locomotion animates a driven rig by itself.
      if (forceAnimation && !_driving)
      {
        if (IsInRange(_navMeshAgent.transform, follow, _stopDistance, 0.1f))
        {
          _animationController?.Play(AnimationKinds.Idle);
        }
        else
        {
          _animationController?.Play(_moveAnimationKind);
        }
      }
    }

    private IEnumerator FollowTargetCoroutine(Transform follow, bool debug)
    {
      _navMeshAgent.enabled = true;
      _navMeshAgent.SetDestination(follow.position);
      if (debug)
      {
        Debug.Log("Set destination: " + follow.position);
      }

      WaitForSeconds wait = new(MonitorFrequency);

      while (true)
      {
        yield return wait;

        if (follow == null)
        {
          StopFollow();
          if (debug)
          {
            Debug.Log("Follow target is null, stopping follow.");
          }

          break;
        }

        if (!_followingActive)
        {
          yield return wait;
          continue;
        }

        if (IsInRange(_navMeshAgent.transform, follow, _stopDistance, 0f))
        {
          _navMeshAgent.enabled = false;
          if (!_driving) _animationController?.Play(AnimationKinds.Idle);
          LookAtTarget(follow);
          if (debug)
          {
            Debug.Log("Reached follow target: " + follow.name);
          }
        }
        else
        {
          _navMeshAgent.enabled = true;

          _navMeshAgent.SetDestination(follow.position);

          if (debug)
          {
            Debug.Log("Following target Set destination: " + follow.name);
          }
        }
      }
    }

    public void StopFollow()
    {
      _followingActive = false;

      if (_tweenLookAt.isAlive)
      {
        _tweenLookAt.Stop();
      }

      if (_navMeshAgent.isActiveAndEnabled && _navMeshAgent.isOnNavMesh)
      {
        _navMeshAgent.enabled = false;
      }

      if (_followCoroutine != null)
      {
        StopCoroutine(_followCoroutine);
        _followCoroutine = null;
      }
    }

    public bool IsFollowing()
    {
      return _followCoroutine != null;
    }

    private void OnDisable()
    {
      StopFollow();
      _playedKind = null;
    }

    private void LookAtTarget(Transform target)
    {
      if (target == null || transform == null)
      {
        return;
      }

      // Calculate the direction to the target
      Vector3 direction = target.position - transform.position;
      if (direction.sqrMagnitude < 0.0001f)
      {
        return; // LookRotation on a zero vector logs its own warning
      }

      // Calculate the target rotation
      Quaternion targetRotation = Quaternion.LookRotation(direction);

      // Quaternion == uses a ~1e-6 dot epsilon, far tighter than PrimeTween's
      // endValue-equals-current check - near-identical targets slipped through
      // and spammed its warning every re-issue. Half a degree is invisible.
      if (Quaternion.Angle(transform.rotation, targetRotation) < 0.5f)
      {
        return;
      }

      if (_tweenLookAt.isAlive)
      {
        _tweenLookAt.Stop();
      }

      // Smoothly rotate towards the target using PrimeTween
      _tweenLookAt = Tween.Rotation(transform, endValue: targetRotation, 0.2f, ease: Ease.OutSine);
    }

    public void SetStoppingDistance(float distance)
    {
      _navMeshAgent.stoppingDistance = distance;
    }

    public void StopAll()
    {
      ClearDestination();
      StopFollow();
    }

    /// <summary>The agent is rooted (it cannot move) or free again at its own speed.</summary>
    public void Root(bool root)
    {
      _rooted = root;
      ApplySpeed();
    }

    private void ApplySpeed()
    {
      if (_navMeshAgent == null) return;
      _navMeshAgent.speed = _rooted ? 0f : _baseSpeed * _timeScale;
    }

    public bool MakeSureOnNavMesh()
    {
      if (_navMeshAgent.isOnNavMesh == false)
      {
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 10f, NavMesh.AllAreas))
        {
          _navMeshAgent.Warp(hit.position);
          return true;
        }

        return false;
      }

      return true;
    }

    public async UniTaskVoid MoveAgentManually(Vector3 targetPosition, float duration, int avoidancePriority,
      CancellationToken? ct, Ease ease = Ease.InOutSine)
    {
      if (!NavMesh.SamplePosition(targetPosition, out NavMeshHit hit, 10f, NavMesh.AllAreas))
      {
        return;
      }

      int currentAvoidancePriority = -1;
      if (_navMeshAgent != null && _navMeshAgent.avoidancePriority != avoidancePriority)
      {
        // Set the avoidance priority if it is different
        currentAvoidancePriority = _navMeshAgent.avoidancePriority;
        _navMeshAgent.avoidancePriority = avoidancePriority;
      }

      // Stop any current navigation (the agent off while the tween moves it);
      // the caller's own clip owns the rig meanwhile.
      ClearDestination();
      StopFollow();
      IDisposable hold = HoldLocomotion();

      // Tween movement using PrimeTween and await its completion
      try
      {
        if (_dashTween.isAlive)
        {
          _dashTween.Stop();
        }

        _dashTween = Tween.Position(transform, hit.position, duration, ease);

        if (_timeBundle != null)
        {
          _timeBundle.TimeScaleTween.Add(_dashTween);
        }

        await _dashTween.ToYieldInstruction().ToUniTask(cancellationToken: ct ?? CancellationToken.None);
      }
      catch (OperationCanceledException)
      {
      }
      finally
      {
        hold.Dispose();
        if (_navMeshAgent != null)
        {
          _navMeshAgent.Warp(hit.position);

          if (currentAvoidancePriority != -1)
          {
            _navMeshAgent.avoidancePriority = currentAvoidancePriority;
          }
        }
      }
    }


    public void RegisterTimeContext(TimeBundle timeBundle)
    {
      UnRegisterTimeContext();

      _timeBundle = timeBundle;
      _timeBundle.TimeContext.OnTimeScaleChanged += OnTimeScaleChanged;
    }

    public void UnRegisterTimeContext()
    {
      if (_timeBundle != null)
      {
        _timeBundle.TimeContext.OnTimeScaleChanged -= OnTimeScaleChanged;
        _timeBundle = null;
      }
    }

    /// <summary>
    /// Moves the target position towards the agent by a fixed distance to find a better approach point
    /// </summary>
    /// <param name="targetPosition">The position we want to approach</param>
    /// <param name="moveDistance">Distance to move the point towards the agent</param>
    /// <returns>The NavMesh point moved towards the agent</returns>
    public Vector3 GetClosestNavMeshPoint(Vector3 targetPosition, float moveDistance)
    {
      Vector3 agentPosition = transform.position;

      // Calculate direction from target to agent
      Vector3 directionToAgent = (agentPosition - targetPosition).normalized;

      // Move the target point towards the agent by the specified distance
      Vector3 movedPoint = targetPosition + (directionToAgent * moveDistance);

      // Try to find a NavMesh point at the moved position
      if (NavMesh.SamplePosition(movedPoint, out NavMeshHit hit, 5f, NavMesh.AllAreas))
      {
        return hit.position;
      }

      // If that fails, try the original target position
      if (NavMesh.SamplePosition(targetPosition, out NavMeshHit originalHit, 5f, NavMesh.AllAreas))
      {
        return originalHit.position;
      }

      // Fallback to the moved point even if not on NavMesh
      return movedPoint;
    }

    private static bool IsInRange(Transform a, Transform b, float range, float tolerance)
    {
      float rangeSqr = (range + tolerance) * (range + tolerance);
      return (a.position - b.position).sqrMagnitude <= rangeSqr;
    }

    // Time stopped: the follow pauses and locomotion freezes; time again:
    // only what was running comes back, at the agent's own speed scaled.
    private void OnTimeScaleChanged(float timeScale)
    {
      if (_navMeshAgent == null) return;

      if (timeScale <= float.Epsilon)
      {
        if (_paused) return;
        _paused = true;
        _followBeforePause = _followCoroutine != null;
        _agentBeforePause = _navMeshAgent.enabled;
        StopFollow();
        return;
      }

      _timeScale = timeScale;
      ApplySpeed();
      _navMeshAgent.angularSpeed = AngularSpeed * timeScale;
      _navMeshAgent.acceleration = Acceleration * timeScale;

      if (!_paused) return;
      _paused = false;
      _playedKind = null;
      if (_followBeforePause && _follow != null) FollowTarget(_follow, false, false);
      else if (_agentBeforePause) _navMeshAgent.enabled = true;
      _followBeforePause = false;
    }
  }
}
