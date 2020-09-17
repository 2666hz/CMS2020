using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SimulationManager : MonoBehaviour
{
	[Header("Initial values")]
	public int trailTexSize = 1024;
	public int numberOfParticles = 524280;
	public ComputeShader computeShader;
	public RenderTexture trailTexture;

	[Header("Run time parameters")]
	public bool m_active = true;
	public bool m_restart = false;
	[Range(0f, 1.0f)] public float startRadius = 0.5f;
	[Range(0f, 1f)] public float deposit = 1.0f;
	[Range(0f, 1f)] public float decay = 0.002f;
	[Range(0f, 180f)] public float sensorAngleDegrees = 45f;  //in degrees
	[Range(0f, 180f)] public float rotationAngleDegrees = 45f;//in degrees
	[Range(0f, 0.1f)] public float sensorOffsetDistance = 0.01f;
	[Range(0f, 0.01f)] public float stepSize = 0.001f;
	[Range(0f, 4294967296.0f)] public float randomness = 0;

	[Header("Interaction")]
	public GameObject Pointer;
	[Range(0f, 1f)] public float pointerRadius = 0.01f;
	[Range(-1.0f, 1f)] public float pointerChemicalA = 0.0f;
	[Range(-1.0f, 1f)] public float pointerParticleAttraction = 0.0f;

	private float sensorAngle;              //in radians
	private float rotationAngle;            //in radians
	private Vector2 pointerUV;

	private int kernelParticleInit, kernelParticleStep, kernelTrailInit, kernelTrailStep;
	private ComputeBuffer particleBuffer;
	private int _particleCount = 0;

	// See PARTICLETHREADSPERGROUP and TRAILTHREADSPERGROUP in Simulation.compute
	private static readonly int m_particleThreadsPerGroup = 64; // { 64, 1, 1 };
	private int m_particleThreadGroups = 0; // = numberOfParticles / ParticleThreadsPerGroup
	private static readonly int[] m_trailThreadsPerGroup = { 8, 8 };
	private int[] m_trailThreadGroups = { 0, 0 }; // = trailTexSize / TrailThreadsPerGroup

	struct Particle
	{
		public Vector2 point;
		public float angle;

		public Particle(Vector2 pos, float angle)
		{
			point = pos;
			this.angle = angle;
		}
	};

	void OnValidate() // Called by Unity when someone changes a value in the Editor
	{
		// Did trailTexSize change?
		if (trailTexture != null && trailTexture.width != trailTexSize)
			InitializeTrail();

		// Did particle count change?
		if (particleBuffer != null && numberOfParticles != _particleCount)
			InitializeParticles();

		if (m_restart)
		{
			m_restart = false;
			ResetSimulation();
		}
	}

	// Start is called before the first frame update
	void Start()
	{
		if (computeShader == null)
		{
			Debug.LogError("Simulation requires computerShader to work.");
			this.enabled = false;
			return;
		}

		// Compute shader connections...
		kernelParticleInit = computeShader.FindKernel("InitParticles");
		kernelParticleStep = computeShader.FindKernel("MoveParticles");
		kernelTrailStep = computeShader.FindKernel("StepTrail");
		kernelTrailInit = computeShader.FindKernel("InitTrail");

		UpdateRuntimeParameters();

		InitializeParticles();
		InitializeTrail();
	}

	// Update is called once per frame
	void Update()
	{
		UpdatePointers();
		UpdateRuntimeParameters();

		if (m_active)
		{
			UpdateParticles();
			UpdateTrail();
		}
	}

	void UpdateRuntimeParameters()
	{
		computeShader.SetFloat("deltaTime", Time.deltaTime);
		sensorAngle = sensorAngleDegrees * 0.0174533f;
		rotationAngle = rotationAngleDegrees * 0.0174533f;
		computeShader.SetFloat("sensorAngle", sensorAngle);
		computeShader.SetFloat("rotationAngle", rotationAngle);
		computeShader.SetFloat("sensorOffsetDistance", sensorOffsetDistance);
		computeShader.SetFloat("stepSize", stepSize);
		computeShader.SetFloat("decay", decay);
		computeShader.SetFloat("deposit", deposit);
		computeShader.SetFloat("startRadius", startRadius);
		computeShader.SetFloat("randomness", randomness);
		computeShader.SetVector("pointerUV", pointerUV);
		computeShader.SetFloat("pointerRadius", pointerRadius);
		computeShader.SetFloat("pointerChemicalA", pointerChemicalA);
		computeShader.SetFloat("pointerParticleAttraction", pointerParticleAttraction);
	}

	void InitializeParticles()
	{
		// Max number of thread groups per dimension is 65535 in D3D11
		// D3D11_CS_DISPATCH_MAX_THREAD_GROUPS_PER_DIMENSION (65535).

		if (numberOfParticles > m_particleThreadsPerGroup * 65535)
			numberOfParticles = m_particleThreadsPerGroup * 65535;

		m_particleThreadGroups = numberOfParticles / m_particleThreadsPerGroup;
		Debug.Log("Particles: " + numberOfParticles + " Thread groups: " + m_particleThreadGroups);

		Particle[] data = new Particle[numberOfParticles];
		particleBuffer = new ComputeBuffer(data.Length, System.Runtime.InteropServices.Marshal.SizeOf<Particle>());
		particleBuffer.SetData(data);
		_particleCount = numberOfParticles;

		//initialize particles with random positions
		computeShader.SetInt("numberOfParticles", numberOfParticles);
		computeShader.SetBuffer(kernelParticleInit, "particleBuffer", particleBuffer);
		computeShader.SetBuffer(kernelParticleStep, "particleBuffer", particleBuffer);
		computeShader.EnableKeyword("Mode1");

		Dispatch(kernelParticleInit, m_particleThreadGroups, 1, 1);
	}

	void InitializeTrail()
	{
		// Ensure our trail map is not smaller than one thread-group (8x8)
		int minSize = m_trailThreadsPerGroup.Max();
		if (trailTexSize < minSize) trailTexSize = minSize;

		bool recreateTexture = false;

		if (trailTexture == null)
		{
			trailTexture = new RenderTexture(trailTexSize, trailTexSize, 24); //, RenderTextureFormat.R8); //, RenderTextureFormat.ARGBFloat);
			trailTexture.name = "TrailMap";

			recreateTexture = true;
		}
		else if (trailTexture.enableRandomWrite == false || trailTexture.width != trailTexSize)
		{
			recreateTexture = true;
		}

		if (recreateTexture)
		{
			trailTexture.Release();
			trailTexture.enableRandomWrite = true;
			trailTexture.depth = 0;
			trailTexture.width = trailTexSize;
			trailTexture.height = trailTexSize;
			trailTexture.wrapMode = TextureWrapMode.Repeat;
			trailTexture.Create();

			computeShader.SetTexture(kernelParticleStep, "TrailMap", trailTexture);
			computeShader.SetTexture(kernelTrailStep, "TrailMap", trailTexture);
			computeShader.SetTexture(kernelTrailInit, "TrailMap", trailTexture);

			// Tell the computeShader how big our trailmap is..
			float f = trailTexSize;
			computeShader.SetFloat("fTrailMapDimension", f);
			computeShader.SetInt("iTrailMapDimension", trailTexSize);
			f = 1 / f;
			computeShader.SetFloat("trailMapTexelSize", f);

			// Set the TrailMap as the texture of the material of this objects
			var rend = GetComponent<Renderer>();
			rend.material.mainTexture = trailTexture;

			Debug.Log("Created " + trailTexture + " with " + trailTexture.format);
		}

		m_trailThreadGroups[0] = trailTexSize / m_trailThreadsPerGroup[0];
		m_trailThreadGroups[1] = trailTexSize / m_trailThreadsPerGroup[1];

		Dispatch(kernelTrailInit, m_trailThreadGroups[0], m_trailThreadGroups[1], 1);
	}

	void ResetSimulation()
	{
		Dispatch(kernelParticleInit, m_particleThreadGroups, 1, 1);
		Dispatch(kernelTrailInit, m_trailThreadGroups[0], m_trailThreadGroups[1], 1);
	}

	void UpdateParticles()
	{
		Dispatch(kernelParticleStep, m_particleThreadGroups, 1, 1);
	}

	void UpdateTrail()
	{
		Dispatch(kernelTrailStep, m_trailThreadGroups[0], m_trailThreadGroups[1], 1);
	}

	void Dispatch(int kernelIndex, int threadGroupsX, int threadGroupsY, int threadGroupsZ, string kernelName = null)
	{
		if (kernelName != null)
			Debug.Log("Dispatch" + kernelName + m_trailThreadGroups[0] + "x" + m_trailThreadGroups[1] + "x 1 threadgroups");

		computeShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
	}

/*	void OnDrawGizmos()
	{
		// Draw a yellow sphere at the transform's position
		Gizmos.color = Color.yellow;
		Gizmos.DrawSphere(Pointer.transform.position, 1);
	}
	*/
	void UpdatePointers()
	{
		if (Pointer == null)
			return;


		// We are going to shoot a ray out from our Pointer GameObject, in the Forward (+Z) direction 
		// and calculate where it intersects with our Simulation "Plane" object.

		RaycastHit hit;

		// Pointer is GameObject. Every GameObject has a member struct called "transform"
		Vector3 end = Pointer.transform.position + Pointer.transform.forward * 10;
		Debug.DrawLine(Pointer.transform.position, end, Color.green);

		if (!Physics.Raycast(Pointer.transform.position, Pointer.transform.forward, out hit))
		{
			// No intersection.. Lets change the color to green and return
			Pointer.GetComponent<Renderer>().material.color = Color.green;
			return;
		}
		// Note, we could also use a ray cast from camera through the mouse also... 
		//	if (!Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit))

		// Hit!.  Change colour of Pointer to red and draw a red line to the hit point
		Pointer.GetComponent<Renderer>().material.color = Color.red;
		Debug.DrawLine(Pointer.transform.position, hit.point, Color.red);

		// Retrieve the UV coordinates of the hit point. These are the simulation-space coordinates.
		pointerUV = hit.textureCoord;
	}

	void OnDestroy()
	{
		if (particleBuffer != null)
			particleBuffer.Release();

		if (trailTexture)
			trailTexture.Release();
	}
}
// public class Physarum : SimulationManager