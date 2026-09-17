namespace UColliders.OBBTree {

	/// <summary>
	/// The <c>OBBTreeNode</c> class represents an element in the OBB tree structure.
	/// </summary>
	[System.Serializable]
	public class OBBTreeNode
	{
		/// <summary>
		/// The OBB associated with this node.
		/// </summary>
		public OBB obb;

		/// <summary>
		/// The array of children for this OBBTreeNode. This array can be:
		/// <list type="bullet">
		/// <item>
		/// <description>An array is of size 0 when the node cannot have children.</description>
		/// </item>
		/// <item>
		/// <description>An array of size 2 and of value <c>{ null, null }</c> 
		/// if the children are not computed yet.</description>
		/// </item>
		/// <item>
		/// <description>An array of size 2 and not null values if they are children OBBs.</description>
		/// </item>
		/// </list>
		/// </summary>
		public OBBTreeNode[] children;

		/// <summary>
		/// An unique id representing the position in the binary tree.
		/// This id is a binary number.
		/// </summary>
		public string id;

		/// <summary>
		/// Initializes the <c>OBBTreeNode</c>, sets the array of children to 
		/// <c>{ null, null }</c>.
		/// </summary>
		public OBBTreeNode(string id)
		{
			this.id = id;
			children = new OBBTreeNode[] { null, null };
		}
	}
}