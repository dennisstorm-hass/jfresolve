using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Services;

/// <summary>
/// Factory for creating and managing circuit breakers
/// </summary>
public class CircuitBreakerFactory
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new();
    private readonly ILoggerFactory _loggerFactory;

    public CircuitBreakerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public CircuitBreaker GetOrCreate(string name)
    {
        return _breakers.GetOrAdd(name, n =>
        {
            var logger = _loggerFactory.CreateLogger<CircuitBreaker>();
            return new CircuitBreaker(
                n,
                logger,
                Constants.CircuitBreakerFailureThreshold,
                Constants.CircuitBreakerOpenDuration,
                Constants.CircuitBreakerHalfOpenTimeout);
        });
    }
}

/// <summary>
/// Circuit breaker pattern implementation to prevent cascading failures
/// States: Closed (normal), Open (failing), HalfOpen (testing)
/// </summary>
public class CircuitBreaker
{
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly string _name;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly TimeSpan _halfOpenTimeout;
    
    private int _failureCount;
    private DateTime _lastFailureTime;
    private CircuitState _state;
    private readonly object _lock = new object();
    private DateTime? _halfOpenTestStartTime;

    public CircuitBreaker(
        string name,
        ILogger<CircuitBreaker> logger,
        int failureThreshold = 5,
        TimeSpan? openDuration = null,
        TimeSpan? halfOpenTimeout = null)
    {
        _name = name;
        _logger = logger;
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromMinutes(1);
        _halfOpenTimeout = halfOpenTimeout ?? TimeSpan.FromSeconds(30);
        _state = CircuitState.Closed;
        _failureCount = 0;
        _lastFailureTime = DateTime.MinValue;
    }

    public CircuitState State => _state;

    /// <summary>
    /// Executes an operation through the circuit breaker
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, Func<Task<T>>? fallback = null)
    {
        // Check circuit state before executing
        if (_state == CircuitState.Open)
        {
            // Check if we should transition to HalfOpen
            if (DateTime.UtcNow - _lastFailureTime >= _openDuration)
            {
                lock (_lock)
                {
                    if (_state == CircuitState.Open && DateTime.UtcNow - _lastFailureTime >= _openDuration)
                    {
                        _state = CircuitState.HalfOpen;
                        _halfOpenTestStartTime = DateTime.UtcNow;
                        _logger.LogInformation("Circuit breaker {Name} transitioning to HalfOpen", _name);
                    }
                }
            }
            else
            {
                // Circuit is open, fail fast
                _logger.LogWarning("Circuit breaker {Name} is Open, failing fast", _name);
                if (fallback != null)
                {
                    return await fallback();
                }
                throw new CircuitBreakerOpenException($"Circuit breaker {_name} is Open");
            }
        }

        try
        {
            var result = await operation();
            
            // Success - reset failure count and close circuit if it was half-open
            OnSuccess();
            return result;
        }
        catch (Exception ex)
        {
            // Failure - increment count and potentially open circuit
            OnFailure();
            
            // If we have a fallback, use it
            if (fallback != null)
            {
                _logger.LogWarning(ex, "Circuit breaker {Name} operation failed, using fallback", _name);
                return await fallback();
            }
            
            throw;
        }
    }

    /// <summary>
    /// Executes an operation that returns void through the circuit breaker
    /// </summary>
    public async Task ExecuteAsync(Func<Task> operation, Func<Task>? fallback = null)
    {
        await ExecuteAsync(async () =>
        {
            await operation();
            return true;
        }, fallback != null ? async () => { await fallback(); return true; } : null);
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                // Success in half-open state - close the circuit
                _state = CircuitState.Closed;
                _failureCount = 0;
                _halfOpenTestStartTime = null;
                _logger.LogInformation("Circuit breaker {Name} closed after successful half-open test", _name);
            }
            else if (_state == CircuitState.Closed)
            {
                // Reset failure count on success
                _failureCount = 0;
            }
        }
    }

    private void OnFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;

            if (_state == CircuitState.HalfOpen)
            {
                // Failure in half-open state - open the circuit immediately
                _state = CircuitState.Open;
                _halfOpenTestStartTime = null;
                _logger.LogWarning("Circuit breaker {Name} opened after failure in half-open state", _name);
            }
            else if (_state == CircuitState.Closed && _failureCount >= _failureThreshold)
            {
                // Too many failures - open the circuit
                _state = CircuitState.Open;
                _logger.LogWarning("Circuit breaker {Name} opened after {FailureCount} failures", _name, _failureCount);
            }
        }
    }

    /// <summary>
    /// Manually reset the circuit breaker (for testing or manual recovery)
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
            _lastFailureTime = DateTime.MinValue;
            _halfOpenTestStartTime = null;
            _logger.LogInformation("Circuit breaker {Name} manually reset", _name);
        }
    }
}

public enum CircuitState
{
    Closed,   // Normal operation
    Open,     // Failing, reject requests
    HalfOpen  // Testing if service recovered
}

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}
