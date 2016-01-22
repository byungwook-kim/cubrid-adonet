﻿/*
 * Copyright (C) 2008 Search Solution Corporation. All rights reserved by Search Solution. 
 *
 * Redistribution and use in source and binary forms, with or without modification, 
 * are permitted provided that the following conditions are met: 
 *
 * - Redistributions of source code must retain the above copyright notice, 
 *   this list of conditions and the following disclaimer. 
 *
 * - Redistributions in binary form must reproduce the above copyright notice, 
 *   this list of conditions and the following disclaimer in the documentation 
 *   and/or other materials provided with the distribution. 
 *
 * - Neither the name of the <ORGANIZATION> nor the names of its contributors 
 *   may be used to endorse or promote products derived from this software without 
 *   specific prior written permission. 
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND 
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED 
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. 
 * IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, 
 * INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, 
 * OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY 
 * OF SUCH DAMAGE. 
 *
 */

using System;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace CUBRID.Data.CUBRIDClient
{
  //[ToolboxBitmap(typeof(CUBRIDConnection), "CUBRIDClient.resources.conn.bmp")]
  /// <summary>
  ///   CUBRID implementation of the <see cref="T:System.Data.Common.DbConnection" /> class.
  /// </summary>
  [DesignerCategory("Code")]
  [ToolboxItem(true)]
  public sealed class CUBRIDConnection : DbConnection, ICloneable
  {
    #region Delegates

    /// <summary>
    ///   Represents the method that will handle the <see cref="CUBRIDConnection.CUBRIDInfoMessageEventArgs" /> event of a 
    ///   <see cref="CUBRIDConnection" />.
    /// </summary>
    public delegate void CUBRIDInfoMessageEventHandler(object sender, CUBRIDInfoMessageEventArgs args);

    #endregion

    private const int _defaultPort = 33000;

    private static readonly byte[] driverInfo =
      {
        (byte) 'C',
        (byte) 'U',
        (byte) 'B',
        (byte) 'R',
        (byte) 'K', 
        (byte) 5, //"OLEDB" client
        0x40 | 0x03, //TODO Document this
        0,
        0,
        0
      };

    private readonly CUBRIDConnectionProperties connProperties;

    private bool autoCommit = true;
    private byte[] brokerInfo;
    private Encoding connEncoding = Encoding.Default;
    private ConnectionState connState = ConnectionState.Closed;
    private string connString;
    private int connTimeout = 30; //seconds
    private string database = "";
    private string db_version = "";
    private byte[] dbinfo;
    private CUBRIDIsolationLevel isolationLevel = CUBRIDIsolationLevel.TRAN_REP_CLASS_UNCOMMIT_INSTANCE;
    private bool keepConnectionAlive;
    private int lockTimeout = 30 * 1000; //the connection lock timeout as milliseconds.
    private const int maxStringLength = Int16.MaxValue;
    private string password = "";
    private int port = _defaultPort;
    private CUBRIDSchemaProvider schemaProvider;
    private string server = "localhost";
    private const string serverVersion = "";
    private int sessionId;
    private Socket socket;
    private bool statementPooling;
    internal CUBRIDStream stream;
    private string user = "public";

    /// <summary>
    ///   Initializes a new instance of the <see cref="CUBRIDConnection" /> class.
    /// </summary>
    public CUBRIDConnection()
    {
      Trace.WriteLineIf(Utils.TraceState, String.Format("CUBRIDConnection"));
      connProperties = new CUBRIDConnectionProperties();
    }

    /// <summary>
    ///   Initializes a new instance of the <see cref="CUBRIDConnection" /> class.
    /// </summary>
    /// <param name="connString"> The connection string. </param>
    public CUBRIDConnection(string connString)
      : this()
    {
      ConnectionString = connString;
    }

    #region ICloneable

    /// <summary>
    ///   Creates a new object that is a copy of the current instance.
    /// </summary>
    /// <returns> A new object that is a copy of this instance. </returns>
    object ICloneable.Clone()
    {
      return Clone();
    }

    /// <summary>
    ///   Creates a CUBRIDConnection clone
    /// </summary>
    /// <returns> CUBRIDConnection clone </returns>
    public CUBRIDConnection Clone()
    {
      return new CUBRIDConnection(connString);
      //We will let the user Open() the connection clone
    }

    #endregion

    #region IDisposeable

    /// <summary>
    ///   Releases the unmanaged resources used by the <see cref="T:System.ComponentModel.Component" /> and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing"> true to release both managed and unmanaged resources; false to release only unmanaged resources. </param>
    protected override void Dispose(bool disposing)
    {
      //Send listeners
      foreach (TraceListener listener in Trace.Listeners)
      {
        listener.Flush();
      }

      if (connState == ConnectionState.Open)
      {
        Trace.WriteLineIf(Utils.TraceState, String.Format("Dispose::Close conn"));
        Close();
      }
      base.Dispose(disposing);
    }

    #endregion

    /// <summary>
    ///   Gets or sets the session id.
    /// </summary>
    /// <value> The session id. </value>
    public int SessionId
    {
      get { return sessionId; }
      set { sessionId = value; }
    }

    /// <summary>
    ///   Gets or sets the database version.
    /// </summary>
    /// <value> The database version. </value>
    public string DbVersion
    {
      get { return db_version; }
      set { db_version = value; }
    }

    /// <summary>
    ///   Gets or sets the logTraceApi flag.
    /// </summary>
    /// <value> The value of the LogTraceApi flag. </value>
    public bool LogTraceApi
    {
      get { return connProperties.LogTraceApi; }
    }

    /// <summary>
    ///   Gets or sets the logTraceNetwork flag.
    /// </summary>
    /// <value> The value of the LogTraceNetwork flag. </value>
    public bool LogTraceNetwork
    {
      get { return connProperties.LogTraceNetwork; }
    }

    /// <summary>
    ///   Gets or sets the string used to open the connection.
    /// </summary>
    /// <returns> The connection string used to establish the initial connection. The exact contents of the connection string depend on the specific data source for this connection. The default value is an empty string. </returns>
    [Browsable(true)]
    [Category("Data")]
    [Description(
      "Information used to connect to a database, such as 'server=<xxx>;port=99999;user=<yyy>;password=<zzz>;database=<dbname>'."
      )]
    public override string ConnectionString
    {
      get { return connString; }
      set
      {
        if (connState != ConnectionState.Closed)
        {
          throw new CUBRIDException(Utils.GetStr(MsgId.NotAllowedToChangeConnectionStringWhenStateIs) + ": " + State +
                                    ".");
        }
        if (value == null)
        {
            throw new CUBRIDException("Connection string is null.");
        }
        connString = value;
        connProperties.Reset(); // reset properties

        ParseConnectionString(); // parser connection string
      }
    }

    /// <summary>
    ///   Gets the time to wait while establishing a connection before terminating the attempt and generating an error.
    /// </summary>
    /// <returns> The time (in seconds) to wait for a connection to open. The default value is determined by the specific type of connection that you are using. </returns>
    [Browsable(true)]
    public override int ConnectionTimeout
    {
      get { return connTimeout; }
    }

    /// <summary>
    ///   Gets the database schema provider.
    /// </summary>
    /// <returns> The current database schema provider. </returns>
    public CUBRIDSchemaProvider SchemaProvider
    {
      get { return schemaProvider ?? (schemaProvider = new CUBRIDSchemaProvider(this)); }
    }

    /// <summary>
    ///   Gets/Sets the connection lock timeout(Unit: milliseconds).
    /// </summary>
    [Browsable(true)]
    public int LockTimeout
    {
      get { return lockTimeout; }
      set { lockTimeout = value; }
    }

    /*
         * Delete this method
         * http://jira.cubrid.org/browse/APIS-485
    /// <summary>
    /// Sets the lock timeout.
    /// </summary>
    /// <param name="value">The value.</param>
    public void SetLockTimeout(int value)
    {
      if (this.connState != ConnectionState.Closed)
      {
        throw new CUBRIDException(Utils.GetStr(MsgId.NotAllowedToChangeLockTimeoutWhenStateIs) + ": " + State + ".");
      }
      if (value >= 1 && value <= 99)
      {
        this.lockTimeout = value;
      }
      else
      {
        throw new Exception(Utils.GetStr(MsgId.InvalidTimeoutValue));
      }
    }
        */

    /// <summary>
    ///   Gets the name of the current database after a connection is opened, or the database name specified in the connection string before the connection is opened.
    /// </summary>
    /// <returns> The name of the current database or the name of the database to be used after a connection is opened. The default value is an empty string. </returns>
    [Browsable(true)]
    public override string Database
    {
      get { return database; }
    }

    /// <summary>
    ///   Gets a string that describes the state of the connection.
    /// </summary>
    /// <returns> The state of the connection. The format of the string returned depends on the specific type of connection you are using. </returns>
    [Browsable(false)]
    public override ConnectionState State
    {
      get { return connState; }
    }

    /// <summary>
    ///   Gets a string that represents the version of the server to which the object is connected.
    /// </summary>
    /// <returns> The version of the database. The format of the string returned depends on the specific type of connection you are using. </returns>
    [Browsable(false)]
    public override string ServerVersion
    {
        get 
        {
            if (State != ConnectionState.Open)
            {
                throw new CUBRIDException(Utils.GetStr(MsgId.TheConnectionIsNotOpen));
            }
            return db_version; 
        }
    }

    /// <summary>
    ///   Gets the name of the database server to which to connect.
    /// </summary>
    /// <returns> The name of the database server to which to connect. The default value is an empty string. </returns>
    [Browsable(true)]
    public override string DataSource
    {
      get { return server; }
    }

    /// <summary>
    ///   Gets the value indicating whether auto commit is set.
    /// </summary>
    /// <value> <c>true</c> if auto commit is set; otherwise, <c>false</c> . </value>
    [Browsable(true)]
    public bool AutoCommit
    {
      get { return autoCommit; }
    }

    /// <summary>
    ///   Gets or sets the length of the max string.
    /// </summary>
    /// <value> The length of the max string. </value>
    public int MaxStringLength
    {
      get { return maxStringLength; }
    }

    /// <summary>
    ///   Gets the <see cref="T:System.Data.Common.DbProviderFactory" /> for this <see cref="T:System.Data.Common.DbConnection" />.
    /// </summary>
    /// <returns> A <see cref="T:System.Data.Common.DbProviderFactory" /> . </returns>
    protected override DbProviderFactory DbProviderFactory
    {
      get { return CUBRIDClientFactory.Instance; }
    }

    /// <summary>
    /// </summary>
    internal CUBRIDStream Stream
    {
      get
      {
        stream.ClearBuffer();

        return stream;
      }
    }

    /// <summary>
    ///   Gets or sets the isolation level.
    /// </summary>
    /// <value> The isolation level. </value>
    public CUBRIDIsolationLevel IsolationLevel
    {
      get { return isolationLevel; }
      set { isolationLevel = value; }
    }

    /// <summary>
    ///   Sets the connection timeout.
    /// </summary>
    /// <param name="value"> The value. </param>
    public void SetConnectionTimeout(int value)
    {
      if (connState != ConnectionState.Closed)
      {
        throw new CUBRIDException(Utils.GetStr(MsgId.NotAllowedToChangeConnectionTimeoutWhenStateIs) + ": " + State +
                                  ".");
      }
      if (value >= 1 && value <= 99)
      {
        connTimeout = value;
      }
      else
      {
        throw new Exception(Utils.GetStr(MsgId.InvalidTimeoutValue));
      }
    }

    private void SetConnectionState(ConnectionState value)
    {
      if (connState != value)
      {
        connState = value;
      }
    }

    /// <summary>
    ///   Changes the current database for an open connection.
    /// </summary>
    /// <param name="databaseName"> Specifies the name of the database for the connection. </param>
    public override void ChangeDatabase(string databaseName)
    {
      throw new NotSupportedException(Utils.GetStr(MsgId.CantChangeDatabase));
    }

    /// <summary>
    ///   Starts a database transaction.
    /// </summary>
    /// <param name="isolationLevel"> Specifies the isolation level for the transaction. </param>
    /// <returns> An object representing the new transaction. </returns>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
      autoCommit = false;
      CUBRIDIsolationLevel level;
      //TODO Verify these mappings
      switch (isolationLevel)
      {
        case System.Data.IsolationLevel.Chaos:
          throw new CUBRIDException(Utils.GetStr(MsgId.NotSupportedInCUBRID));
        case System.Data.IsolationLevel.ReadCommitted:
          level = CUBRIDIsolationLevel.TRAN_REP_CLASS_COMMIT_INSTANCE;
          break;
        case System.Data.IsolationLevel.ReadUncommitted:
          level = CUBRIDIsolationLevel.TRAN_REP_CLASS_UNCOMMIT_INSTANCE;
          break;
        case System.Data.IsolationLevel.RepeatableRead:
          level = CUBRIDIsolationLevel.TRAN_REP_CLASS_REP_INSTANCE;
          break;
        case System.Data.IsolationLevel.Serializable:
          level = CUBRIDIsolationLevel.TRAN_SERIALIZABLE;
          break;
        case System.Data.IsolationLevel.Snapshot:
          throw new CUBRIDException(Utils.GetStr(MsgId.NotSupportedInCUBRID));
        case System.Data.IsolationLevel.Unspecified:
          level = CUBRIDIsolationLevel.TRAN_UNKNOWN_ISOLATION;
          break;
        default:
          level = CUBRIDIsolationLevel.TRAN_REP_CLASS_REP_INSTANCE;
          break;
      }

      return new CUBRIDTransaction(this, level);
    }

    /// <summary>
    ///   Begins the transaction.
    /// </summary>
    /// <returns> </returns>
    public new CUBRIDTransaction BeginTransaction()
    {
      return BeginTransaction(GetIsolationLevel());
    }

    /// <summary>
    ///   Begins the transaction.
    /// </summary>
    /// <param name="iso"> The isolation level. </param>
    /// <returns> </returns>
    public CUBRIDTransaction BeginTransaction(CUBRIDIsolationLevel iso)
    {
      if (connState != ConnectionState.Open)
        throw new InvalidOperationException(Utils.GetStr(MsgId.TheConnectionIsNotOpen));

      autoCommit = false;
      CUBRIDTransaction t = new CUBRIDTransaction(this, iso);

      return t;
    }

    /// <summary>
    ///   Closes the connection to the database. This is the preferred method of closing any open connection.
    /// </summary>
    /// <exception cref="T:System.Data.Common.DbException">The connection-level error that occurred while opening the connection.</exception>
    public override void Close()
    {
      if (connState == ConnectionState.Closed)
        return;

      CloseInternal();
      SetConnectionState(ConnectionState.Closed);
    }

    /// <summary>
    ///   Creates and returns a <see cref="T:System.Data.Common.DbCommand" /> object associated with the current connection.
    /// </summary>
    /// <returns> A <see cref="T:System.Data.Common.DbCommand" /> object. </returns>
    protected override DbCommand CreateDbCommand()
    {
      return CreateCommand();
    }

    /// <summary>
    ///   Creates a CUBRIDCommand.
    /// </summary>
    /// <returns> </returns>
    public new CUBRIDCommand CreateCommand()
    {
      CUBRIDCommand cmd = new CUBRIDCommand();
      cmd.Connection = this;

      return cmd;
    }

    /// <summary>
    ///   Opens a database connection with the settings specified by the <see
    ///    cref="P:System.Data.Common.DbConnection.ConnectionString" />.
    /// </summary>
    public override void Open()
    {
      Trace.WriteLineIf(Utils.TraceState,
                        String.Format(
                          "Open::Connection parameters: server: {0}, port: {1}, database: {2}, user: {3}, password: {4}",
                          server, port, database, user, password));

      if (State == ConnectionState.Open)
      {
        throw new InvalidOperationException(Utils.GetStr(MsgId.ConnectionAlreadyOpen));
      }

      SetConnectionState(ConnectionState.Connecting);

      keepConnectionAlive = false;
      statementPooling = false;

      stream = new CUBRIDStream();

      try
      {
        try
        {
          Reconnect();
        }
        catch (Exception ex)
        {
          if (connProperties.AlterHostCount() > 0)
          {
            for (int i = 0; i < connProperties.AltHosts.Length; i++)
            {
              string[] pair = connProperties.AltHosts[i].Split(':');
              if (pair.Length == 2)
              {
                server = pair[0];
                port = Convert.ToInt32(pair[1]);
              }
              else
              {
                port = _defaultPort;
                server = pair[0];
              }

              try
              {
                Reconnect();
                if (connState == ConnectionState.Open)
                  break;
              }
              catch (Exception exp)
              {
                if (i == connProperties.AltHosts.Length - 1)
                {
                  throw exp;
                }
              }
            }
          }
          else
          {
            throw ex;
          }
        }

        EndTransaction(CCITransactionType.CCI_TRAN_COMMIT);
      }
      catch (Exception)
      {
        SetConnectionState(ConnectionState.Closed);
        throw;
      }
    }

    internal void Abort()
    {
      try
      {
        Close();
      }
      catch (Exception ex)
      {
        Trace.WriteLineIf(Utils.TraceState, "Error occurred aborting the connection. Exception was: " + ex.Message);
      }
      SetConnectionState(ConnectionState.Closed);
    }

    private void ParseConnectionString()
    {
      char[] delimiter = { ';' };
      string[] tokens = connString.Split(delimiter);

      if (tokens[0] == connString)
      {
        // URL mode
        // parse url string
        string[] tokenURL = connString.Split('?');
        if (tokenURL.Length == 2)
        {
          tokens = tokenURL[0].Split(':');
          {
            // default connection properties
            server = tokens[2]; // server
            port = Convert.ToInt32(tokens[3]); // broker port
            database = tokens[4]; // database
            user = tokens[5]; // user
            password = tokens[6]; // password
          }

          // parse connection properties
          connProperties.SetConnectionProperties(tokenURL[1]);
          // auto-commit
          autoCommit = connProperties.AutoCommit;

          if (connProperties.LoginTimeout >= 1000)
            connTimeout = connProperties.LoginTimeout / 1000;

          if (connProperties.LogTraceApi)
          {
            CUBRIDTrace.WriteLine(String.Format("URL[{0}]", connString));
          }
        }
      }
      else
      {
        //If the connection string ends with (;) remove last token
        if (tokens[tokens.Length - 1] == String.Empty)
          Array.Resize(ref tokens, tokens.Length - 1);
        foreach (string t in tokens)
        {
          string[] pair = t.Split('=');
          if (pair.Length == 2)
          {
            if (pair[0].ToLower().Equals("server"))
            {
              server = pair[1];
            }
            else if (pair[0].ToLower().Equals("database"))
            {
              database = pair[1];
            }
            else if (pair[0].ToLower().Equals("port"))
            {
              port = Convert.ToInt32(pair[1]);
            }
            else if (pair[0].ToLower().Equals("user"))
            {
              user = pair[1];
            }
            else if (pair[0].ToLower().Equals("password"))
            {
              password = pair[1];
            }
            else if (pair[0].ToLower().Equals("charset"))
            {
              connEncoding = EncodingFromString(pair[1]);
            }
            else if (pair[0].ToLower().Equals("autocommit"))
            {
              autoCommit = Convert.ToInt32(pair[1]) != 0;
            }
          }
          else
          {
            throw new CUBRIDException(Utils.GetStr(MsgId.InvalidConnectionParameter) + " : " + t);
          }
        }
      }

      if (database == string.Empty || database.Length == 0)
      {
        throw new CUBRIDException(Utils.GetStr(MsgId.DBNameIsEmpty));
      }
    }

    /// <summary>
    /// </summary>
    /// <param name="encoding"> </param>
    /// <returns> </returns>
    private Encoding EncodingFromString(string encoding)
    {
      encoding = encoding.ToLower();
      switch (encoding)
      {
        case "utf-8":
          return Encoding.UTF8;
        case "cp1252":
          return Encoding.GetEncoding("Windows-1252");
        case "iso-8859-1":
          return Encoding.GetEncoding("iso-8859-1");
        case "euc-kr":
          return Encoding.GetEncoding("euc-kr");
        case "euc-jp":
          return Encoding.GetEncoding("euc-jp");
        case "gb2312":
          return Encoding.GetEncoding("gb2312");
        case "gbk":
          return Encoding.GetEncoding("gbk");
        default:
          return Encoding.Default;
      }
    }

    /// <summary>
    ///   Sets the charset used to communicate with the database
    /// </summary>
    public void SetEncoding(string name)
    {
      connEncoding = EncodingFromString(name);
    }

    /// <summary>
    /// </summary>
    /// <returns> </returns>
    public Encoding GetEncoding()
    {
      return connEncoding;
    }

    /// <summary>
    /// </summary>
    private void Reconnect()
    {
      // connect
      Connect(server, port);

      // send drvier information
      int newPort = SendDriverInfo();

      if (newPort < 0)
      {
        throw new CUBRIDException(Utils.GetStr(MsgId.InvalidConnectionPort));
      }

      if (newPort > 0)
      {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 1));
        // Changed to LingerOption(true, 1)
        socket.Close();

        Connect(server, newPort);
      }

      if (connTimeout > 0)
        stream.baseStream.ReadTimeout = connTimeout * 1000;

      // send db inforamtion
      SendDbInfo();
      // receive broker information
      ReceiveBrokerInfo();

      // get db version
      db_version = stream.GetDbVersion();

      // Reset network stream read timeout
      stream.baseStream.ReadTimeout = connProperties.QueryTimeout > 0 ? connProperties.QueryTimeout : Timeout.Infinite;

      //Set Auto Commit option
      //SetAutoCommit(this.autoCommit);

      //Set Isolation level option
      //TODO
      SetIsolationLevel(isolationLevel);

      //TODO
      SetLockTimeout(lockTimeout);

      //TODO
      //Not supported yet: SetMaxStringLength
      //SetMaxStringLength(this.maxStringLength);

      SetConnectionState(ConnectionState.Open);
    }

    /// <summary>
    ///   Connect to the CUBRID DB.
    /// </summary>
    /// <param name="strServer"> The host name/IP. </param>
    /// <param name="port"> The broker port. </param>
    private void Connect(string strServer, int port)
    {
      try
      {
        SetConnectionState(ConnectionState.Connecting);

        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(strServer, port);
        socket.NoDelay = true;
        stream.ClearBuffer(new NetworkStream(socket, true));

        SetConnectionState(ConnectionState.Open);
      }
      catch (Exception e)
      {
        SetConnectionState(ConnectionState.Closed);

        throw new CUBRIDException(e.Message);
      }
    }

    /// <summary>
    ///   Send Driver information to CUBRID Database.
    /// </summary>
    /// <returns> </returns>
    private int SendDriverInfo()
    {
      try
      {
        stream.WriteBytesToRaw(driverInfo, 0, driverInfo.Length);
        //this.stream.SendData(); //8.2
        stream.Stream.Flush(); //8.4

        return stream.ReadIntFromRaw();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Receive broker information.
    /// </summary>
    private void ReceiveBrokerInfo()
    {
      try
      {
        stream.Receive();

        brokerInfo = stream.ReadBytes(8);

        //private final static int BROKER_INFO_DBMS_TYPE = 0;
        //private final static int BROKER_INFO_KEEP_CONNECTION = 1;
        //private final static int BROKER_INFO_STATEMENT_POOLING = 2;
        //private final static int BROKER_INFO_CCI_PCONNECT = 3;
        //private final static int BROKER_INFO_MAJOR_VERSION = 4;
        //private final static int BROKER_INFO_MINOR_VERSION = 5;
        //private final static int BROKER_INFO_PATCH_VERSION = 6;
        //private final static int BROKER_INFO_RESERVED = 7;
        keepConnectionAlive = (brokerInfo[1] == 1);
        statementPooling = (brokerInfo[2] == 1);

        sessionId = stream.ReadInt();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    private void SendDbInfo()
    {
      // see broker/cas_protocol.h
      // #define SRV_CON_DBNAME_SIZE 32
      // #define SRV_CON_DBUSER_SIZE 32
      // #define SRV_CON_DBPASSWD_SIZE 32
      // #define SRV_CON_DBSESS_ID_SIZE 20
      // #define SRV_CON_URL_SIZE 512
      // #define SRV_CON_DB_INFO_SIZE \
      // (SRV_CON_DBNAME_SIZE + SRV_CON_DBUSER_SIZE +
      // SRV_CON_DBPASSWD_SIZE + \
      // SRV_CON_URL_SIZE + SRV_CON_DBSESS_ID_SIZE)
      //dbInfo = new byte[32 + 32 + 32 + 512 + 20]; 96+512+20 = 96+532 = 628
      //UJCIUtil.copy_byte(dbInfo, 0, 32, dbname);
      //UJCIUtil.copy_byte(dbInfo, 32, 32, user);
      //UJCIUtil.copy_byte(dbInfo, 64, 32, passwd);
      //UJCIUtil.copy_byte(dbInfo, 96, 512, url);
      //UJCIUtil.copy_byte(dbInfo, 608, 20, new Integer(sessionId).toString());

      dbinfo = new byte[32 + 32 + 32 + 512 + 20];
      for (int i = 0; i < 628; i++)
      {
        dbinfo[i] = 0;
      }

      byte[] dbBytes = Encoding.ASCII.GetBytes(database);
      Array.Copy(dbBytes, 0, dbinfo, 0, dbBytes.Length);
      byte[] userBytes = Encoding.ASCII.GetBytes(user);
      Array.Copy(userBytes, 0, dbinfo, 32, userBytes.Length);
      byte[] passBytes = Encoding.ASCII.GetBytes(password);
      Array.Copy(passBytes, 0, dbinfo, 64, passBytes.Length);

      //string url = "jdbc:cubrid:localhost:30000:demodb:public::"; //8.4 (we will use a default JDBC-type value)
      string url = "jdbc:cubrid:" + server + ":" + port + ":" + database + ":" +
                   user + ":********:";
      byte[] urlBytes = Encoding.ASCII.GetBytes(url); //8.4
      Array.Copy(urlBytes, 0, dbinfo, 96, urlBytes.Length); //8.4

      try
      {
        stream.WriteBytesToRaw(dbinfo, 0, dbinfo.Length);
        stream.Stream.Flush(); //8.4
      }
      catch (SocketException ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Sets the isolation level.
    /// </summary>
    /// <param name="level"> The level. </param>
    public void SetIsolationLevel(CUBRIDIsolationLevel level)
    {
      isolationLevel = level;
      SetDBParameter(CCIDbParam.CCI_PARAM_ISOLATION_LEVEL, (int)level);
    }

    /// <summary>
    ///   Gets the isolation level.
    /// </summary>
    /// <returns> </returns>
    public CUBRIDIsolationLevel GetIsolationLevel()
    {
      int level = GetDBParameter(CCIDbParam.CCI_PARAM_ISOLATION_LEVEL);
      isolationLevel = (CUBRIDIsolationLevel)level;
      return isolationLevel;
    }

    /// <summary>
    ///   Specifies the connection lock timeout as seconds.
    /// </summary>
    /// <param name="value"> lock timeout value(Unit: seconds). </param>
    public void SetLockTimeout(int value)
    {
      if (value <= 0)
      {
        return;
      }

      lockTimeout = value;
      SetDBParameter(CCIDbParam.CCI_PARAM_LOCK_TIMEOUT, LockTimeout);
    }

    /// <summary>
    ///   Gets system parameter: CCI_PARAM_LOCK_TIMEOUT.
    /// </summary>
    /// <returns> Parameter value </returns>
    public int GetLockTimeout()
    {
      lockTimeout = GetDBParameter(CCIDbParam.CCI_PARAM_LOCK_TIMEOUT);
      return lockTimeout;
    }

    /// <summary>
    ///   Configures a system parameter
    /// </summary>
    /// <param name="param_name"> System parameter name. </param>
    /// <param name="nValue"> Parameter value </param>
    private void SetDBParameter(CCIDbParam param_name, int nValue)
    {
      if (connState != ConnectionState.Open)
        throw new InvalidOperationException(Utils.GetStr(MsgId.TheConnectionIsNotOpen));

      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_SET_DB_PARAMETER);
        stream.WriteIntArg((int)param_name);
        stream.WriteIntArg(nValue);
        stream.Send();

        stream.Receive();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Gets a parameter specified in the database.
    /// </summary>
    /// <param name="param_name"> System parameter name. </param>
    /// <returns> Parameter value </returns>
    private int GetDBParameter(CCIDbParam param_name)
    {
      if (connState != ConnectionState.Open)
        throw new InvalidOperationException(Utils.GetStr(MsgId.TheConnectionIsNotOpen));

      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_GET_DB_PARAMETER);
        stream.WriteIntArg((int)param_name);
        stream.Send();

        stream.Receive();
        return stream.ReadInt();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /*
         * APIS-500: data will still be rollbacked after setting setautocommit(true)
         */

    /// <summary>
    ///   Sets the auto commit state of the connection.
    /// </summary>
    public void SetAutoCommit(bool autoCommit)
    {
      if (connState != ConnectionState.Open)
        throw new InvalidOperationException(Utils.GetStr(MsgId.TheConnectionIsNotOpen));

      try
      {
        if (autoCommit != this.autoCommit)
        {
          EndTransaction(CCITransactionType.CCI_TRAN_COMMIT);
          this.autoCommit = autoCommit;
        }
        /*this.stream.WriteCommand(CASFunctionCode.CAS_FC_SET_DB_PARAMETER);
                    this.stream.WriteIntArg((int)CCIDbParam.CCI_PARAM_AUTO_COMMIT);
                    int p = autoCommit ? (int)1 : (int)0;
                    this.stream.WriteIntArg(p);
                    this.stream.Send();

                    int res = this.stream.Receive();*/
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Gets the auto commit state of the connection.
    /// </summary>
    public bool GetAutoCommit()
    {
      return autoCommit;
    }

    /// <summary>
    ///   Sets the max length of the string used for communicating with the database.
    /// </summary>
    /// <param name="val"> The max length value. </param>
    public void SetMaxStringLength(int val)
    {
      //TODO
      throw new NotImplementedException();
      /*
      try
      {
        this.stream.WriteCommand(CASFunctionCode.CAS_FC_SET_DB_PARAMETER);
        this.stream.WriteIntArg((int)CCIDbParam.CCI_PARAM_MAX_STRING_LENGTH);
        this.stream.WriteIntArg(val);
        this.stream.Send();

        int res = this.stream.Receive();
        this.maxStringLength = val;
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
      */
    }

    /// <summary>
    ///   Gets the length of the max string.
    /// </summary>
    public void GetMaxStringLength()
    {
      //TODO
      throw new NotImplementedException();
      /*
      try
      {
        this.stream.WriteCommand(CASFunctionCode.CAS_FC_GET_DB_PARAMETER);
        this.stream.WriteIntArg((int)CCIDbParam.CCI_PARAM_MAX_STRING_LENGTH);
        this.stream.Send();

        int res = this.stream.Receive();
        int val = this.stream.ReadInt();
        this.maxStringLength = val;
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
      */
    }

    /// <summary>
    ///   Batch executes sql statements, without returning any resultsets.
    /// </summary>
    /// <param name="sqls"> The SQL commands to be executed. </param>
    /// <returns> </returns>
    public int BatchExecuteNoQuery(string[] sqls)
    {
      try
      {
        return stream.RequestBatchExecute(sqls, autoCommit);
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Commits the transaction.
    /// </summary>
    public void Commit()
    {
      EndTransaction(CCITransactionType.CCI_TRAN_COMMIT);
      autoCommit = true;
    }

    /// <summary>
    ///   Rollbacks the transaction.
    /// </summary>
    public void Rollback()
    {
      EndTransaction(CCITransactionType.CCI_TRAN_ROLLBACK);
      autoCommit = true;
    }

    private void EndTransaction(CCITransactionType type)
    {
      stream.ClearBuffer();

      stream.WriteCommand(CASFunctionCode.CAS_FC_END_TRAN);
      stream.WriteByteArg((byte)type);

      stream.Send();

      if (type == CCITransactionType.CCI_TRAN_COMMIT || type == CCITransactionType.CCI_TRAN_ROLLBACK)
      {
        stream.Receive();
      }

      if (!keepConnectionAlive)
      {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 1));
        // Changed SocketOptionLevel from 'Tcp' to 'Socket' and changed 'true' to LingerOption(true, 1)
        socket.Close();
        socket = null;
        SetConnectionState(ConnectionState.Closed);
      }
    }

    internal bool CloseInternal()
    {
      try
      {
        if (socket != null)
        {
          stream.ClearBuffer();

          stream.WriteCommand(CASFunctionCode.CAS_FC_CON_CLOSE);
          stream.Send();
          stream.Receive();

          socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 1));
          // Changed to LingerOption(true, 1)
          socket.Close();
          socket = null;
          SetConnectionState(ConnectionState.Closed);
        }
        return true;
      }
      catch (Exception ex)
      {
        Trace.WriteLineIf(Utils.TraceState,
                          String.Concat("Error occurred aborting the conn. Exception was: ", ex.Message));

        return false;
      }
    }

    /// <summary>
    ///   Gets the table name from OID.
    /// </summary>
    /// <param name="oidStr"> The OID. </param>
    /// <returns> </returns>
    public string GetTableNameFromOID(string oidStr)
    {
      try
      {
        int res = 0;

        if (socket != null)
        {
          CUBRIDOid cubridOid = new CUBRIDOid(oidStr);
          stream.ClearBuffer();

          stream.WriteCommand(CASFunctionCode.CAS_FC_OID_CMD);
          stream.WriteByteArg((byte)OidCommand.GET_CLASS_NAME_BY_OID);
          stream.WriteOidArg(cubridOid);
          stream.Send();

          res = stream.Receive();
        }

        if (res >= 0)
        {
          string tablename = stream.ReadString(stream.RemainedCapacity(), GetEncoding());

          return tablename;
        }

        return null;
      }
      catch (Exception ex)
      {
        Trace.WriteLineIf(Utils.TraceState, "Exception occured: " + ex.Message);
        return null;
      }
    }

    internal void ReconnectIfNeeded()
    {
      if (socket == null)
      {
        Reconnect();
      }
    }

    /// <summary>
    ///   Gets the current database.
    /// </summary>
    /// <returns> </returns>
    public string CurrentDatabase()
    {
      if (!string.IsNullOrEmpty(Database))
        return Database;

      using (CUBRIDCommand cmd = new CUBRIDCommand("select database()", this))
      {
        object _obj = cmd.ExecuteScalar();
        if (_obj != null)
        {
          return _obj.ToString();
        }
      }
      return string.Empty;
    }


    /// <summary>
    ///   Adds the element to set.
    /// </summary>
    /// <param name="oid"> The oid. </param>
    /// <param name="attributeName"> Name of the attribute. </param>
    /// <param name="value"> The value. </param>
    public void AddElementToSet(CUBRIDOid oid, String attributeName, Object value)
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_COLLECTION);
        stream.WriteByteArg((byte)CUBRIDCollectionCommand.ADD_ELEMENT_TO_SET);
        stream.WriteOidArg(oid);
        if (attributeName == null)
        {
          stream.WriteNullArg();
        }
        else
        {
          stream.WriteStringArg(attributeName, GetEncoding());
        }
        CUBRIDParameter p = new CUBRIDParameter(attributeName, value);
        p.Write(stream);
        stream.Send();

        stream.Receive();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Drops the element in set.
    /// </summary>
    /// <param name="oid"> The oid. </param>
    /// <param name="attributeName"> Name of the attribute. </param>
    /// <param name="value"> The value. </param>
    public void DropElementInSet(CUBRIDOid oid, String attributeName, Object value)
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_COLLECTION);
        stream.WriteByteArg((byte)CUBRIDCollectionCommand.DROP_ELEMENT_IN_SET);
        stream.WriteOidArg(oid);
        stream.WriteStringArg(attributeName, GetEncoding());
        CUBRIDParameter p = new CUBRIDParameter(attributeName, value);
        p.Write(stream);
        stream.Send();

        stream.Receive();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Adds the element in sequence.
    /// </summary>
    /// <param name="oid"> The oid. </param>
    /// <param name="attributeName"> Name of the attribute. </param>
    /// <param name="index"> The index. </param>
    /// <param name="value"> The value. </param>
    public void UpdateElementInSequence(CUBRIDOid oid, String attributeName, int index, Object value)
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_COLLECTION);
        stream.WriteByteArg((byte)CUBRIDCollectionCommand.PUT_ELEMENT_ON_SEQUENCE);
        stream.WriteOidArg(oid);
        stream.WriteIntArg(index);
        if (attributeName == null)
        {
          stream.WriteNullArg();
        }
        else
        {
          stream.WriteStringArg(attributeName, GetEncoding());
        }
        CUBRIDParameter p = new CUBRIDParameter(attributeName, CUBRIDDataType.CCI_U_TYPE_INT);
        p.Value = value;
        p.Write(stream);
        stream.Send();

        stream.Receive();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Inserts the element in sequence.
    /// </summary>
    /// <param name="oid"> The oid. </param>
    /// <param name="attributeName"> Name of the attribute. </param>
    /// <param name="index"> The index. </param>
    /// <param name="value"> The value. </param>
    public void InsertElementInSequence(CUBRIDOid oid, String attributeName, int index, Object value)
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_COLLECTION);
        stream.WriteByteArg((byte)CUBRIDCollectionCommand.INSERT_ELEMENT_INTO_SEQUENCE);
        stream.WriteOidArg(oid);
        stream.WriteIntArg(index);
        if (attributeName == null)
        {
          stream.WriteNullArg();
        }
        else
        {
          stream.WriteStringArg(attributeName, GetEncoding());
        }
        CUBRIDParameter p = new CUBRIDParameter(attributeName, value);
        p.Write(stream);
        stream.Send();

        stream.Receive();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Drops the element in sequence.
    /// </summary>
    /// <param name="oid"> The oid. </param>
    /// <param name="attributeName"> Name of the attribute. </param>
    /// <param name="index"> The index. </param>
    public void DropElementInSequence(CUBRIDOid oid, String attributeName, int index)
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_COLLECTION);
        stream.WriteByteArg((byte)CUBRIDCollectionCommand.DROP_ELEMENT_IN_SEQUENCE);
        stream.WriteOidArg(oid);
        stream.WriteIntArg(index);
        if (attributeName == null)
        {
          stream.WriteNullArg();
        }
        else
        {
          stream.WriteStringArg(attributeName, GetEncoding());
        }

        stream.Send();

        stream.Receive();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Gets the size of the collection.
    /// </summary>
    /// <param name="oid"> The oid. </param>
    /// <param name="attributeName"> Name of the attribute. </param>
    /// <returns> </returns>
    public int GetCollectionSize(CUBRIDOid oid, String attributeName)
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_COLLECTION);
        stream.WriteByteArg((byte)CUBRIDCollectionCommand.GET_SIZE_OF_COLLECTION);
        stream.WriteOidArg(oid);
        if (attributeName == null)
        {
          stream.WriteNullArg();
        }
        else
        {
          stream.WriteStringArg(attributeName, GetEncoding());
        }
        stream.Send();

        stream.Receive();

        return stream.ReadInt();
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Gets the query plan only.
    /// </summary>
    /// <param name="sql"> The SQL. </param>
    /// <returns> </returns>
    public String GetQueryPlanOnly(String sql)
    {
      if (String.IsNullOrEmpty(sql))
        return null;

      try
      {
        //outBuffer.newRequest(UFunctionCode.GET_QUERY_INFO);
        //outBuffer.addInt(0);
        //outBuffer.addByte(UStatement.QUERY_INFO_PLAN);
        //outBuffer.addStringWithNull(sql); 
        stream.WriteCommand(CASFunctionCode.CAS_FC_GET_QUERY_INFO);
        stream.WriteIntArg(0);
        stream.WriteByteArg(1);
        stream.WriteStringArg(sql, GetEncoding());
        stream.Send();

        stream.Receive();

        return stream.ReadString(stream.RemainedCapacity(), GetEncoding());
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Gets the database version.
    /// </summary>
    /// <returns> </returns>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public String GetDatabaseProductVersion()
    {
      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_GET_DB_VERSION);
        stream.WriteByteArg(AutoCommit ? (byte)1 : (byte)0);
        stream.Send();

        stream.Receive();
        int len = stream.RemainedCapacity();

        return stream.ReadString(len, GetEncoding());
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    /// <summary>
    ///   Create a new LOB.
    /// </summary>
    /// <param name="lob_type"> The LOB type. </param>
    /// <returns> The packed LOB handle. </returns>
    public byte[] LOBNew(CUBRIDDataType lob_type)
    {
      if (lob_type != CUBRIDDataType.CCI_U_TYPE_BLOB && lob_type != CUBRIDDataType.CCI_U_TYPE_CLOB)
      {
        throw new ArgumentException(Utils.GetStr(MsgId.NotAValidLOBType));
      }

      stream.WriteCommand(CASFunctionCode.CAS_FC_LOB_NEW);
      stream.WriteIntArg((int)lob_type);

      stream.Send();

      int res = stream.Receive();
      if (res < 0)
      {
        return null;
      }

      byte[] packedLobHandle = new byte[res];
      stream.ReadBytes(packedLobHandle);

      return packedLobHandle;
    }

    /// <summary>
    ///   LOB read.
    /// </summary>
    /// <param name="packedLobHandle"> The packed LOB handle. </param>
    /// <param name="offset"> The offset. </param>
    /// <param name="buf"> The buffer. </param>
    /// <param name="start"> The start positon. </param>
    /// <param name="len"> The length. </param>
    /// <returns> </returns>
    public int LOBRead(byte[] packedLobHandle, long offset, byte[] buf, int start, int len)
    {
      stream.WriteCommand(CASFunctionCode.CAS_FC_LOB_READ);
      stream.WriteBytesArg(packedLobHandle);
      stream.WriteLongArg(offset);
      stream.WriteIntArg(len);
      stream.Send();

      int res = stream.Receive();
      if (res < 0)
      {
        throw new CUBRIDException(CUBRIDException.CUBRIDErrorCode.ER_UNKNOWN,
                                  Utils.GetStr(MsgId.ErrorWrittingLOBContent));
      }

      stream.ReadBytes(buf, start, res);

      return res;
    }

    /// <summary>
    ///   LOB write.
    /// </summary>
    /// <param name="packedLobHandle"> The packed LOB handle. </param>
    /// <param name="offset"> The offset. </param>
    /// <param name="buf"> The buffer. </param>
    /// <param name="start"> The start position. </param>
    /// <param name="len"> The length. </param>
    /// <returns> </returns>
    public int LOBWrite(byte[] packedLobHandle, long offset, byte[] buf, int start, int len)
    {
      stream.WriteCommand(CASFunctionCode.CAS_FC_LOB_WRITE);
      stream.WriteBytesArg(packedLobHandle);
      stream.WriteLongArg(offset);
      stream.WriteBytesArg(buf, start, len);
      stream.Send();

      int res = stream.Receive();
      if (res < 0)
      {
        throw new CUBRIDException(CUBRIDException.CUBRIDErrorCode.ER_UNKNOWN,
                                  Utils.GetStr(MsgId.ErrorWrittingLOBContent));
      }

      return res;
    }

    /// <summary>
    ///   Batch execute statements.
    /// </summary>
    /// <param name="batchSqlStmt"> The batch SQL statements. </param>
    /// <returns> CUBRIDBatchResult </returns>
    public CUBRIDBatchResult BatchExecute(string[] batchSqlStmt)
    {
      if (batchSqlStmt == null || batchSqlStmt.Length == 0)
        return null;

      try
      {
        stream.WriteCommand(CASFunctionCode.CAS_FC_EXECUTE_BATCH);
        stream.WriteByteArg(AutoCommit ? (byte)1 : (byte)0);

        foreach (string t in batchSqlStmt)
        {
          if (t != null)
            stream.WriteStringArg(t, GetEncoding());
          else
            stream.WriteNullArg();
        }

        stream.Send();
        stream.Receive();

        CUBRIDBatchResult batchResult = new CUBRIDBatchResult(stream.ReadInt());
        for (int i = 0; i < batchResult.Count(); i++)
        {
          batchResult.setStatementType(i, stream.ReadByte());
          int result = stream.ReadInt();
          if (result < 0)
          {
            batchResult.setResultError(i, result, stream.ReadString(stream.ReadInt(), connEncoding));
          }
          else
          {
            batchResult.setResultCode(i, result);
            stream.ReadInt(); //TODO Document value
            stream.ReadShort(); //TODO Document value
            stream.ReadShort(); //TODO Document value
          }
        }

        return batchResult;
      }
      catch (Exception ex)
      {
        throw new CUBRIDException(ex.Message);
      }
    }

    #region GetSchema Support

    /// <summary>
    ///   Returns schema information for the data source of this <see cref="DbConnection" />.
    /// </summary>
    /// <returns> A <see cref="DataTable" /> that contains schema information. </returns>
    public override DataTable GetSchema()
    {
      throw new CUBRIDException(Utils.GetStr(MsgId.NotSupportedInCUBRID));
      //return GetSchema(null);
    }

    /// <summary>
    ///   Returns schema information for the data source of this 
    ///   <see cref="DbConnection" /> using the specified string for the schema name.
    /// </summary>
    /// <paramList name="collectionName">Specifies the name of the schema to return.</paramList>
    /// <returns> A <see cref="DataTable" /> that contains schema information. </returns>
    public override DataTable GetSchema(string collectionName)
    {
      return GetSchema(collectionName, null);
    }

    /// <summary>
    ///   Returns schema information for the data source of this <see cref="DbConnection" />
    ///   using the specified string for the schema name and the specified string array 
    ///   for the restriction values.
    /// </summary>
    /// <param name="collectionName"> Specifies the name of the schema to return. </param>
    /// <param name="filters"> Specifies a set of restriction values for the requested schema. </param>
    /// <returns> A <see cref="DataTable" /> that contains schema information. </returns>
    public override DataTable GetSchema(string collectionName, string[] filters)
    {
      if (collectionName == null)
      {
        throw new Exception(Utils.GetStr(MsgId.collectionNameIsNull));
        //return null; //if no collection name is specified, we return null
      }

      if (SchemaProvider != null)
      {
        string[] cleaned_filters = SchemaProvider.CleanFilters(filters);
        DataTable dt = SchemaProvider.GetSchema(collectionName, cleaned_filters);

        return dt;
      }

      return null;
    }

    #endregion

    #region Nested type: CUBRIDInfoMessageEventArgs

    /// <summary>
    ///   Provides data for the InfoMessage event.
    /// </summary>
    public class CUBRIDInfoMessageEventArgs : EventArgs
    {
      /// <summary>
      ///   Collection of errors
      /// </summary>
      public CUBRIDException[] errors;
    }

    #endregion
  }
}