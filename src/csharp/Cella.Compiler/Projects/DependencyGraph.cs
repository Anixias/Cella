namespace Cella.Compiler.Projects;

/// <summary>
/// Represents a directed graph of nodes.
/// </summary>
/// <typeparam name="T">The type of nodes in the graph.</typeparam>
public class DependencyGraph<T> where T : notnull
{
    private readonly Dictionary<T, HashSet<T>> _dependencies = [];
    private readonly HashSet<T> _nodes = [];
    
    /// <summary>
    /// Adds a node to the graph if it doesn't already exist.
    /// </summary>
    /// <param name="node">The node to add.</param>
    /// <returns><see langword="true"/> if the node was added; <see langword="false"/> otherwise.</returns>
    public bool Add(T node) => _nodes.Add(node);
    
    /// <summary>
    /// Adds a dependency from a source node to a target node.
    /// </summary>
    /// <param name="source">The node that depends on the other.</param>
    /// <param name="target">The node that must be resolved first.</param>
    public void AddDependency(T source, T target)
    {
        Add(source);
        Add(target);
        _dependencies.GetOrAdd(source).Add(target);
    }
    
    /// <summary>
    /// Returns nodes in an order such that each node appears only after its dependencies.
    /// </summary>
    /// <returns>An <see cref="IEnumerable{T}"/> of nodes in resolution order.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the graph contains a cycle.</exception>
    public IEnumerable<T> GetResolutionOrder()
    {
        var yieldedCount = 0;
        var unresolvedDependencies = new Dictionary<T, int>();
        var reverse = new Dictionary<T, HashSet<T>>();
        
        foreach (var node in _nodes)
        {
            unresolvedDependencies[node] = _dependencies.TryGetValue(node, out var dependencies)
                ? dependencies.Count
                : 0;
            
            reverse[node] = new HashSet<T>();
        }
        
        foreach (var (dependent, value) in _dependencies)
            foreach (var dependency in value)
                reverse[dependency].Add(dependent);
        
        var queue = new Queue<T>(_nodes.Count);
        foreach (var node in _nodes)
        {
            if (unresolvedDependencies[node] == 0)
                queue.Enqueue(node);
        }
        
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            
            yieldedCount++;
            yield return node;
            
            if (!reverse.TryGetValue(node, out var dependents))
                continue;
            
            foreach (var dependent in dependents)
            {
                unresolvedDependencies[dependent]--;
                if (unresolvedDependencies[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }
        
        if (yieldedCount != _nodes.Count)
            throw new InvalidOperationException("The dependency graph contains a cycle and cannot be resolved.");
    }
    
    public IEnumerable<T> GetDependencies(T item)
    {
        if (!_dependencies.TryGetValue(item, out var dependencies))
            yield break;
        
        foreach (var dependency in dependencies)
            yield return dependency;
    }
}