using System.Reflection;

namespace SubathonManager.Tests
{
    public static class ReflectionExtensions
    {
        public static async Task<object?> InvokePrivate(this object obj, string methodName, params object?[] args)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var type = obj.GetType();
            var method = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == methodName &&
                                     m.GetParameters().Length == args.Length);

            if (method == null)
                throw new MissingMethodException($"No private method '{methodName}' found with {args.Length} parameter(s).");

            var result = method.Invoke(obj, args);

            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    return taskType.GetProperty("Result")!.GetValue(task);
                }
                return null;
            }

            return result;
        }
    }
}