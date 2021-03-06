﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Renci.SshNet.Abstractions;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System;
using System.Diagnostics;
using System.Threading;
using BufferedRead = Renci.SshNet.Sftp.SftpFileReader.BufferedRead;

namespace Renci.SshNet.Tests.Classes.Sftp
{
    [TestClass]
    public class SftpFileReaderTest_ReadAheadEndInvokeException_PreventsFurtherReadAheads : SftpFileReaderTestBase
    {
        private const int ChunkLength = 32 * 1024;

        private byte[] _handle;
        private int _fileSize;
        private byte[] _chunk1;
        private byte[] _chunk3;
        private SftpFileReader _reader;
        private ManualResetEvent _readAheadChunk2;
        private ManualResetEvent _readChunk2;
        private SshException _exception;
        private SshException _actualException;

        protected override void SetupData()
        {
            var random = new Random();

            _handle = CreateByteArray(random, 5);
            _chunk1 = CreateByteArray(random, ChunkLength);
            _chunk3 = CreateByteArray(random, ChunkLength);
            _fileSize = 3 * _chunk1.Length;

            _readAheadChunk2 = new ManualResetEvent(false);
            _readChunk2 = new ManualResetEvent(false);

            _exception = new SshException();
        }

        protected override void SetupMocks()
        {
            var seq = new MockSequence();

            SftpSessionMock.InSequence(seq).Setup(p => p.RequestFStat(_handle)).Returns(CreateSftpFileAttributes(_fileSize));
            SftpSessionMock.InSequence(seq)
                           .Setup(p => p.BeginRead(_handle, 0, ChunkLength, It.IsNotNull<AsyncCallback>(), It.IsAny<BufferedRead>()))
                           .Callback<byte[], ulong, uint, AsyncCallback, object>((handle, offset, length, callback, state) =>
                           {
                               var asyncResult = new SftpReadAsyncResult(callback, state);
                               asyncResult.SetAsCompleted(_chunk1, false);
                           })
                           .Returns((SftpReadAsyncResult)null);
            SftpSessionMock.InSequence(seq)
                            .Setup(p => p.BeginRead(_handle, ChunkLength, ChunkLength, It.IsNotNull<AsyncCallback>(), It.IsAny<BufferedRead>()))
                            .Callback<byte[], ulong, uint, AsyncCallback, object>((handle, offset, length, callback, state) =>
                            {
                                ThreadAbstraction.ExecuteThread(() =>
                                {
                                    // signal that we're in the read-ahead for chunk2
                                    _readAheadChunk2.Set();
                                    // wait for client to start reading this chunk
                                    _readChunk2.WaitOne(TimeSpan.FromSeconds(5));
                                    // sleep a short time to make sure the client is in the blocking wait
                                    Thread.Sleep(500);
                                    // complete async read of chunk2 with exception
                                    var asyncResult = new SftpReadAsyncResult(callback, state);
                                    asyncResult.SetAsCompleted(_exception, false);
                                });
                            })
                           .Returns((SftpReadAsyncResult)null);
            SftpSessionMock.InSequence(seq)
                            .Setup(p => p.BeginRead(_handle, 2 * ChunkLength, ChunkLength, It.IsNotNull<AsyncCallback>(), It.IsAny<BufferedRead>()))
                            .Callback<byte[], ulong, uint, AsyncCallback, object>((handle, offset, length, callback, state) =>
                            {
                                // this chunk should never be read
                                Thread.Sleep(20000);

                                var asyncResult = new SftpReadAsyncResult(callback, state);
                                asyncResult.SetAsCompleted(_chunk3, false);
                            })
                            .Returns((SftpReadAsyncResult)null);

        }

        protected override void Arrange()
        {
            base.Arrange();

            // use a max. read-ahead of 1 to allow us to verify that the next read-ahead is not done
            // when a read-ahead has failed
            _reader = new SftpFileReader(_handle, SftpSessionMock.Object, 1);
        }

        protected override void Act()
        {
            _reader.Read();

            // wait until SftpFileReader has starting reading ahead chunk 2
            Assert.IsTrue(_readAheadChunk2.WaitOne(TimeSpan.FromSeconds(5)));
            // signal that we are about to read chunk 2
            _readChunk2.Set();

            try
            {
                _reader.Read();
                Assert.Fail();
            }
            catch (SshException ex)
            {
                _actualException = ex;
            }
        }

        [TestMethod]
        public void ReadOfSecondChunkShouldThrowExceptionThatOccurredInReadAhead()
        {
            Assert.IsNotNull(_actualException);
            Assert.AreSame(_exception, _actualException);
        }

        [TestMethod]
        public void ReadAfterReadAheadExceptionShouldThrowObjectDisposedException()
        {
            try
            {
                _reader.Read();
                Assert.Fail();
            }
            catch (ObjectDisposedException ex)
            {
                Assert.IsNull(ex.InnerException);
                Assert.AreEqual(typeof(SftpFileReader).FullName, ex.ObjectName);
            }
        }

        [TestMethod]
        public void DisposeShouldCompleteImmediately()
        {
            var stopwatch = Stopwatch.StartNew();
            _reader.Dispose();
            stopwatch.Stop();

            Assert.IsTrue(stopwatch.ElapsedMilliseconds < 200, "Dispose took too long to complete: " + stopwatch.ElapsedMilliseconds);
        }

        [TestMethod]
        public void ExceptionInReadAheadShouldPreventFurtherReadAheads()
        {
            SftpSessionMock.Verify(p => p.BeginRead(_handle, 2 * ChunkLength, ChunkLength, It.IsNotNull<AsyncCallback>(), It.IsAny<BufferedRead>()), Times.Never);
        }
    }
}
