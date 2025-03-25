namespace System.Collections.Generic
{
    public partial class Deque<T>
    {
        #region Node Class

        // Represents a node in the deque.
        [Serializable()]
        public class Node(T value)
        {
            private T value = value;

            private Node? previous = null;

            private Node? next = null;

            public T? Value
            {
                get
                {
                    return value;
                }
                set
                {
                    this.value = value;
                }
            }

            public Node? Previous
            {
                get
                {
                    return previous;
                }
                set
                {
                    previous = value;
                }
            }

            public Node? Next
            {
                get
                {
                    return next;
                }
                set
                {
                    next = value;
                }
            }
        }

        #endregion
    }
}
