using System;
using System.Collections;
using System.Threading;
using CupkekGames.TimeSystem;
using Cysharp.Threading.Tasks;
using PrimeTween;
using UnityEngine;
using UnityEngine.AI;
using CupkekGames.Animations;

namespace CupkekGames.Navigation
{
  [RequireComponent(typeof(NavMeshAgent))]
  public class NavMeshAgentController : MonoBehaviour
  {
    // Static variables
    public static float MonitorFrequency = 0.1f;

    // Steering
    public static float Speed = 1f;
    public static float AngularSpeed = 3200f;
    public static float Acceleration = 8f;

    public static float StoppingDistance = .2f;

    // References
    private NavMeshAgent _navMeshAgent;
    public NavMeshAgent NavMeshAgent => _navMeshAgent;
    private IAnimationStateController _animationController;

    public IAnimationStateController AnimationController => _animationController;

    // Monitor
    private Coroutine _monitorCoroutine;

    // Events
    public event Action OnDestinationReached;

    // Follow
    private Transform _follow;
    private Coroutine _followCoroutine;
    private bool _followingActive = false;

    private float _stopDistance => _navMeshAgent.stoppingDistance;

    // Other
    private Tween _dashTween;

    private Tween _tweenLookAt;

    // Time Context
    private TimeBundle _timeBundle;

    private void Awake()
    {
      _navMeshAgent = GetComponent<NavMeshAgent>();

      _navMeshAgent.enabled = false;

      _navMeshAgent.speed = Speed;
      _navMeshAgent.angularSpeed = AngularSpeed;
      _navMeshAgent.acceleration = Acceleration;
      _navMeshAgent.stoppingDistance = StoppingDistance;
      // _navMeshAgent.radius = Radius;
      // _navMeshAgent.height = Height;

      _animationController = GetComponentInChildren<IAnimationStateController>();
    }

    public void SetAnimationController(IAnimationStateController controller)
    {
      _animationController = controller;
    }

    public void StartMonitoringAgent()
    {
      if (_monitorCoroutine != null)
      {
        return;
      }

      _monitorCoroutine = StartCoroutine(AgentMonitorCoroutine());
    }

    public void StopMonitoringAgent()
    {
      if (_monitorCoroutine != null)
      {
        StopCoroutine(_monitorCoroutine);
        _monitorCoroutine = null;
      }
    }

    private void OnDisable()
    {
      StopMonitoringAgent();
      StopFollow();
    }

    private IEnumerator AgentMonitorCoroutine()
    {
      WaitForSeconds wait = new(MonitorFrequency);

      while (true)
      {
        if (_navMeshAgent.enabled)
        {
          AgentMonitorTick();
        }

        yield return wait;
      }
    }

    private void AgentMonitorTick()
    {
      if (_navMeshAgent.remainingDistance <= _stopDistance)
      {
        _animationController.Play(AnimationKinds.Idle);

        OnDestinationReached?.Invoke();
      }
      else
      {
        _animationController.Play(AnimationKinds.Walk);
      }
    }

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

      _follow = follow;
      _followingActive = true;

      LookAtTarget(follow);

      if (MakeSureOnNavMesh())
      {
        _followCoroutine = StartCoroutine(FollowTargetCoroutine(follow, debug));
      }

      if (forceAnimation)
      {
        if (IsInRange(_navMeshAgent.transform, follow, _stopDistance, 0.1f))
        {
          _animationController.Play(AnimationKinds.Idle);
        }
        else
        {
          _animationController.Play(AnimationKinds.Walk);
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
          _animationController.Play(AnimationKinds.Idle);
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

    private void LookAtTarget(Transform target)
    {
      if (target == null || transform == null)
      {
        return;
      }

      // Calculate the direction to the target
      Vector3 direction = target.position - transform.position;

      // Calculate the target rotation
      Quaternion targetRotation = Quaternion.LookRotation(direction);

      if (transform.rotation == targetRotation)
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
      OnDisable();
    }

    public void Root(bool root)
    {
      if (root)
      {
        _navMeshAgent.speed = 0;
      }
      else
      {
        _navMeshAgent.speed = Speed;
      }
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

      // Stop any current navigation and disable the NavMeshAgent
      StopFollow();
      StopMonitoringAgent();

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

    private void OnTimeScaleChanged(float timeScale)
    {
      if (_navMeshAgent != null)
      {
        if (timeScale <= float.Epsilon)
        {
          StopFollow();
          StopMonitoringAgent();
        }
        else
        {
          float newSpeed = Speed * timeScale;

          _navMeshAgent.speed = newSpeed;
          _navMeshAgent.angularSpeed = AngularSpeed * timeScale;
          _navMeshAgent.acceleration = Acceleration * timeScale;

          StartMonitoringAgent();
          FollowTarget(_follow, false, false);
        }
      }
    }
  }
}