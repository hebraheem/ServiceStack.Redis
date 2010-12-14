//
// https://github.com/mythz/ServiceStack.Redis
// ServiceStack.Redis: ECMA CLI Binding to the Redis key-value storage system
//
// Authors:
//   Demis Bellot (demis.bellot@gmail.com)
//
// Copyright 2010 Liquidbit Ltd.
//
// Licensed under the same terms of Redis and ServiceStack: new BSD license.
//

using System;
using System.Collections.Generic;

namespace ServiceStack.Redis
{
	/// <summary>
	/// Adds support for Redis Transactions (i.e. MULTI/EXEC/DISCARD operations).
	/// </summary>
	public class RedisTransaction
		: RedisAllPurposePipeline, IRedisTransaction, IRedisQueueCompletableOperation
	{
        private int _numCommands = 0;
		public RedisTransaction(RedisClient redisClient) : base(redisClient)
		{
			
		}

        protected override void Init()
        {
           base.Init();
           RedisClient.Multi();
           RedisClient.Transaction = this;
        }

        /// <summary>
        /// Put "QUEUED" messages at back of queue
        /// </summary>
        /// <param name="queued"></param>
        private void QueueExpectQueued()
        {
            var op = new QueuedRedisOperation();
            op.VoidReadCommand = RedisClient.ExpectQueued;
            QueuedCommands.Insert(0, op);
        }

        public void Commit()
        {
            try
            {
                RedisClient.Exec();
                _numCommands = QueuedCommands.Count / 2;
                
                /////////////////////////////////////////////////////
                // Queue up reading of stock multi/exec responses

                //the first half of the responses will be "QUEUED", so insert read count operation
                // after these
                var readCountOp = new QueuedRedisOperation()
                {
                    IntReadCommand = RedisClient.ReadMultiDataResultCount,
                    OnSuccessIntCallback = handleMultiDataResultCount
                };
                QueuedCommands.Insert(_numCommands, readCountOp);

                //handle OK response from MULTI (insert at beginning)
                var readOkOp = new QueuedRedisOperation()
                {
                    VoidReadCommand = RedisClient.ExpectOk,
                };
                QueuedCommands.Insert(0, readOkOp);

                //////////////////////////////
                // flush send buffers
                RedisClient.FlushSendBuffer();

                /////////////////////////////
                //receive expected results
                foreach (var queuedCommand in QueuedCommands)
                {
                    queuedCommand.ProcessResult();
                }
            }
            finally
            {
                RedisClient.Transaction = null;
                base.ClosePipeline();
                RedisClient.AddTypeIdsRegisteredDuringPipeline();
            }
        }

        /// <summary>
        /// callback for after result count is read in
        /// </summary>
        /// <param name="count"></param>
        private void handleMultiDataResultCount(int count)
        {
            if (count != _numCommands)
                throw new InvalidOperationException(string.Format(
                    "Invalid results received from 'EXEC', expected '{0}' received '{1}'"
                    + "\nWarning: Transaction was committed",
                    _numCommands, count));
        }

		public void Rollback()
		{
			if (RedisClient.Transaction == null) 
				throw new InvalidOperationException("There is no current transaction to Rollback");

			RedisClient.Transaction = null;
			RedisClient.ClearTypeIdsRegisteredDuringPipeline();
		}

		public void Dispose()
		{
            base.Dispose();
            if (RedisClient.Transaction == null) return;
		    Rollback();
        }

        #region Overrides of RedisQueueCompletableOperation methods

        protected override void AddCurrentQueuedOperation()
        {
            base.AddCurrentQueuedOperation();
            QueueExpectQueued();
        }
        #endregion
    }
}